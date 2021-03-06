﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeGen
{
    partial class ILBuilder
    {
        internal enum BlockType
        {
            Normal,
            Try,
            Catch,
            Filter,
            Finally,
            Fault,
            Switch
        }

        internal enum Reachability : byte
        {
            /// <summary>
            /// Block is not reachable or reachability analysis
            /// has not been performed.
            /// </summary>
            NotReachable = 0,

            /// <summary>
            /// Block can be reached either falling through
            /// from previous block or from branch.
            /// </summary>
            Reachable,

            /// <summary>
            /// Block is reachable from try or catch but
            /// finally prevents falling through.
            /// </summary>
            BlockedByFinally,
        }

        // internal for testing
        [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
        internal class BasicBlock
        {
            public static readonly ObjectPool<BasicBlock> Pool = CreatePool(32);
            private static ObjectPool<BasicBlock> CreatePool(int size)
            {
                return new ObjectPool<BasicBlock>(() => new PooledBasicBlock(), size);
            }

            protected BasicBlock()
            {
            }

            internal BasicBlock(ILBuilder builder)
            {
                Debug.Assert(BitConverter.IsLittleEndian);
                Initialize(builder);
            }

            internal void Initialize(ILBuilder builder)
            {
                this.builder = builder;
                this.FirstILMarker = -1;
                this.LastILMarker = -1;
            }

            //parent builder
            internal ILBuilder builder;

            private Microsoft.Cci.MemoryStream lazyRegularInstructions;
            public Microsoft.Cci.BinaryWriter Writer
            {
                get
                {
                    if (lazyRegularInstructions == null)
                    {
                        lazyRegularInstructions = Microsoft.Cci.MemoryStream.GetInstance();
                    }

                    return new Microsoft.Cci.BinaryWriter(lazyRegularInstructions);
                }
            }

            // first IL marker for IL offsets in this block or -1
            public int FirstILMarker { get; private set; }

            // last IL marker for IL offsets in this block or -1
            public int LastILMarker { get; private set; }

            public void AddILMarker(int marker)
            {
                //  We assume that all IL markers allocated for the same basic block are 
                //  allocated sequentially and don't interleave with markers from other blocks
                Debug.Assert((this.FirstILMarker < 0) == (this.LastILMarker < 0));
                Debug.Assert((this.LastILMarker < 0) || (this.LastILMarker + 1 == marker));

                if (this.FirstILMarker < 0)
                {
                    this.FirstILMarker = marker;
                }
                this.LastILMarker = marker;
            }

            public void RemoveTailILMarker(int marker)
            {
                //  We assume that all IL markers allocated for the same basic block are 
                //  allocated sequentially and don't interleave with markers from other blocks
                Debug.Assert(this.FirstILMarker >= 0);
                Debug.Assert(this.LastILMarker >= 0);
                Debug.Assert(this.LastILMarker == marker);

                if (this.FirstILMarker == this.LastILMarker)
                {
                    this.FirstILMarker = -1;
                    this.LastILMarker = -1;
                }
                else
                {
                    this.LastILMarker--;
                }
            }

            //next block in the block sequence. Note that it is not necessarily reachable from current block.
            public BasicBlock NextBlock;

            //destination of the exit branch. null if branch code is nop or ret.
            private object branchLabel;

            //block start relative to the method body.
            public int Start;

            //opcode that is reverse to the BranchCode.
            private byte revBranchCode;

            //opcode that terminated the block. nop in a case if block was terminated by a label.
            private ILOpCode branchCode;

            //reachability analysis uses this flag to indicate that the block is reachable.
            internal Reachability Reachability;


            //nearest enclosing exception handler if any
            public virtual ExceptionHandlerScope EnclosingHandler
            {
                get { return null; }
            }

            internal virtual void Free()
            {
                if (lazyRegularInstructions != null)
                {
                    lazyRegularInstructions.Free();
                    lazyRegularInstructions = null;
                }
            }

            public object BranchLabel
            {
                get
                {
                    return this.branchLabel;
                }
            }

            public ILOpCode BranchCode
            {
                get
                {
                    return this.branchCode;
                }
                set
                {
                    this.branchCode = value;
                }
            }

            public ILOpCode RevBranchCode
            {
                get
                {
                    return (ILOpCode)this.revBranchCode;
                }
                set
                {
                    Debug.Assert((ILOpCode)(byte)value == value, "rev opcodes must fit in a byte");
                    this.revBranchCode = (byte)value;
                }
            }

            //destination of the branch. 
            //null if branch code is nop or ret or if label is not yet marked.
            public BasicBlock BranchBlock
            {
                get
                {
                    BasicBlock result = null;

                    if (BranchLabel != null)
                    {
                        result = builder.labelInfos[BranchLabel].bb;
                    }

                    return result;
                }
            }

            public void SetBranchCode(ILOpCode newBranchCode)
            {
                Debug.Assert(this.BranchCode.IsConditionalBranch() == newBranchCode.IsConditionalBranch());
                Debug.Assert(newBranchCode.IsBranchToLabel() == (this.branchLabel != null));

                this.BranchCode = newBranchCode;
            }

            public void SetBranch(object newLabel, ILOpCode branchCode, ILOpCode revBranchCode)
            {
                this.SetBranch(newLabel, branchCode);
                this.RevBranchCode = revBranchCode;
            }

            public void SetBranch(object newLabel, ILOpCode branchCode)
            {
                this.BranchCode = branchCode;

                if (this.branchLabel != newLabel)
                {
                    this.branchLabel = newLabel;

                    if (this.BranchCode.IsConditionalBranch())
                    {
                        Debug.Assert(newLabel != null);

                        var labelInfo = this.builder.labelInfos[newLabel];
                        if (!labelInfo.targetOfConditionalBranches)
                        {
                            this.builder.labelInfos[newLabel] = labelInfo.SetTargetOfConditionalBranches();
                        }
                    }
                }
            }

            /// <summary>
            /// Returns true if this block has a branch label
            /// and is not a "nop" branch.
            /// </summary>
            private bool IsBranchToLabel
            {
                get { return (this.BranchLabel != null) && (this.BranchCode != ILOpCode.Nop); }
            }

            public virtual BlockType Type
            {
                get { return BlockType.Normal; }
            }

            /// <summary>
            /// Instructions that are not branches.
            /// </summary>
            public Microsoft.Cci.MemoryStream RegularInstructions
            {
                get
                {
                    return lazyRegularInstructions;
                }
            }

            /// <summary>
            /// The block contains only the final branch or nothing at all
            /// </summary>
            public bool HasNoRegularInstructions
            {
                get
                {
                    return lazyRegularInstructions == null;
                }
            }

            public uint RegularInstructionsLength
            {
                get
                {
                    var li = lazyRegularInstructions;
                    return li == null ? 0 : li.Length;
                }
            }

            /// <summary>
            /// Updates position of the current block to account for shorter sizes of previous blocks.
            /// </summary>
            /// <param name="delta"></param>
            internal void AdjustForDelta(int delta)
            {
                //blocks can only get shorter.
                Debug.Assert(delta <= 0);

                if (delta != 0)
                {
                    this.Start += delta;
                }
            }

            internal void RewriteBranchesAcrossExceptionHandlers()
            {
                if (this.EnclosingHandler == null)
                {
                    // Cannot branch into a handler.
                    Debug.Assert((BranchBlock == null) || (BranchBlock.EnclosingHandler == null));
                }

                var branchBlock = BranchBlock;
                if (branchBlock == null)
                {
                    return;
                }

                if (branchBlock.EnclosingHandler != this.EnclosingHandler)
                {
                    // Only unconditional branches can be replaced.
                    this.SetBranchCode(this.BranchCode.GetLeaveOpcode());
                }
            }

            /// <summary>
            /// If possible, changes the branch code of the current block to the short version and 
            /// updates the delta correspondingly.
            /// </summary>
            /// <param name="delta">Position delta created by previous block size reductions.</param>
            internal void ShortenBranches(ref int delta)
            {
                //NOTE: current block is supposed to be already adjusted.

                if (!this.IsBranchToLabel)
                {
                    return; //Not a branch;
                }

                var curBranchCode = this.BranchCode;
                if (curBranchCode.BranchOperandSize() == 1)
                {
                    return; //already short;
                }

                // reduction in current block length if changed to short branch.
                // currently all long and short opcodes have same size, so reduction is always -3
                const int reduction = -3;

                int offset;
                var branchBlockStart = BranchBlock.Start;
                if (branchBlockStart > Start)
                {
                    //forward branch
                    //there must be NextBlock
                    // delta will be applied equally to both branchBlock and NextBlock so no need to consider delta here
                    offset = branchBlockStart - NextBlock.Start;
                }
                else
                {
                    //backward branch
                    // delta is already applied to current block and branchBlock so no need to consider delta here
                    offset = branchBlockStart - (this.Start + this.TotalSize + reduction);
                }

                if (unchecked((sbyte)offset == offset))
                {
                    //it fits!
                    this.SetBranchCode(curBranchCode.GetShortOpcode());
                    delta += reduction;
                }
            }

            /// <summary>
            /// replaces branches with more compact code if possible.
            /// * same branch as in the next     ===> nop
            /// * branch to the next block       ===> nop
            /// * branch to ret block            ===> ret
            /// * cond branch over uncond branch ===> flip condition, skip next block
            /// * cond branch to equivalent      ===> pop args + nop
            /// </summary>
            internal bool OptimizeBranches(ref int delta)
            {
                //NOTE: current block is supposed to be already adjusted.

                // we are only interested in branches that go to another block.
                if (this.IsBranchToLabel)
                {
                    Debug.Assert(BranchCode != ILOpCode.Nop, "Nop branches should not have labels");

                    var next = this.NextNontrivial;

                    if (next != null)
                    {
                        // check for next block branching to the same location with the same branch instruction
                        // in such case we can simply drop through.
                        if (TryOptimizeSameAsNext(next, ref delta)) return true;

                        // check for unconditional branch to the next block or to return
                        if (TryOptimizeBranchToNextOrRet(next, ref delta)) return true;

                        // check for branch over uncond branch
                        if (TryOptimizeBranchOverUncondBranch(next, ref delta)) return true;

                        // check for conditional branch to equivalent blocks
                        // in such case we can simply pop condition arguments and drop through.
                        if (TryOptimizeBranchToEquivalent(next, ref delta)) return true;
                    }
                }

                return false;
            }

            private BasicBlock NextNontrivial
            {
                get
                {
                    var next = this.NextBlock;
                    while (next != null &&
                        next.BranchCode == ILOpCode.Nop &&
                        next.HasNoRegularInstructions)
                    {
                        next = next.NextBlock;
                    }

                    return next;
                }
            }

            private bool TryOptimizeSameAsNext(BasicBlock next, ref int delta)
            {
                if (next.HasNoRegularInstructions &&
                    next.BranchCode == this.BranchCode &&
                    next.BranchBlock.Start == this.BranchBlock.Start)
                {
                    if (next.EnclosingHandler == this.EnclosingHandler)
                    {
                        var diff = this.BranchCode.Size() + this.BranchCode.BranchOperandSize();
                        delta -= diff;
                        this.SetBranch(null, ILOpCode.Nop);
                        return true;
                    }
                }

                return false;
            }

            private bool TryOptimizeBranchOverUncondBranch(BasicBlock next, ref int delta)
            {
                if (next.HasNoRegularInstructions &&
                    next.NextBlock != null &&
                    next.NextBlock.Start == BranchBlock.Start &&
                    (next.BranchCode == ILOpCode.Br || next.BranchCode == ILOpCode.Br_s) &&
                    next.BranchBlock != next)
                {
                    ILOpCode revBrOp = this.GetReversedBranchOp();

                    if (revBrOp != ILOpCode.Nop)
                    {
                        // we are effectively removing "next" from the block chain.
                        // that is ok, since branch-to-branch should already eliminate any possible branches to "next"
                        // and it was only reachable from current via NextBlock which we are re-directing.
                        // Also, if there are any blocks between "next" and BranchBlock, they are all empty
                        // so we do not even care if they are reachable or not.
                        Debug.Assert(!builder.labelInfos.Values.Any(li => li.bb == next), "nothing should branch to a branch at this point");

                        var intermediateNext = this.NextBlock;
                        while (intermediateNext != next)
                        {
                            Debug.Assert(intermediateNext.TotalSize == 0);
                            intermediateNext.Reachability = ILBuilder.Reachability.NotReachable;
                            intermediateNext = intermediateNext.NextBlock;
                        }

                        next.Reachability = Reachability.NotReachable;
                        delta -= next.TotalSize;

                        if (next.BranchCode == ILOpCode.Br_s)
                        {
                            revBrOp = revBrOp.GetShortOpcode();
                        }

                        // our next block is now where we used to branch
                        this.NextBlock = BranchBlock;

                        // swap BranchCode for revBrOp
                        // set our branch label where next block used to branch.
                        var origBrOp = this.BranchCode;
                        this.SetBranch(next.BranchLabel, revBrOp, origBrOp);

                        return true;
                    }
                }

                return false;
            }

            private bool TryOptimizeBranchToNextOrRet(BasicBlock next, ref int delta)
            {
                var curBranchCode = this.BranchCode;
                if (curBranchCode == ILOpCode.Br || curBranchCode == ILOpCode.Br_s)
                {
                    // check for branch to next.
                    if (BranchBlock.Start - next.Start == 0)
                    {
                        // becomes a nop block
                        this.SetBranch(null, ILOpCode.Nop);

                        delta -= (curBranchCode.Size() + curBranchCode.BranchOperandSize());
                        return true;
                    }

                    // check for branch to ret.
                    if (BranchBlock.HasNoRegularInstructions && BranchBlock.BranchCode == ILOpCode.Ret)
                    {
                        this.SetBranch(null, ILOpCode.Ret);

                        // curBranchCode.Size() + curBranchCode.BranchOperandSize() - Ret.Size()
                        delta -= (curBranchCode.Size() + curBranchCode.BranchOperandSize() - 1);
                        return true;
                    }
                }

                return false;
            }

            private bool TryOptimizeBranchToEquivalent(BasicBlock next, ref int delta)
            {
                var curBranchCode = this.BranchCode;
                if (curBranchCode.IsConditionalBranch())
                {
                    // check for branch to next, 
                    // or if both blocks are identical
                    if (BranchBlock.Start - next.Start == 0 ||
                        AreIdentical(BranchBlock, next))
                    {
                        // becomes a pop block
                        this.SetBranch(null, ILOpCode.Nop);
                        this.Writer.WriteByte((byte)ILOpCode.Pop);

                        // curBranchCode.Size() + curBranchCode.BranchOperandSize() - ILOpCode.Pop.Size()
                        delta -= (curBranchCode.Size() + curBranchCode.BranchOperandSize() - 1);

                        if (curBranchCode.IsRelationalBranch())
                        {
                            this.Writer.WriteByte((byte)ILOpCode.Pop);
                            delta += 1;
                        }

                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Blocks are identical if:
            /// 1) have same regular instructions
            /// 2) lead to unconditional control transfer (no fall through)
            /// 3) branch with the same instruction to the same label
            /// </summary>
            private bool AreIdentical(BasicBlock one, BasicBlock another)
            {
                if (one.branchCode == another.branchCode &&
                     !one.branchCode.CanFallThrough() &&
                     one.branchLabel == another.branchLabel)
                {
                    var instr1 = one.RegularInstructions;
                    var instr2 = another.RegularInstructions;

                    if (instr1 == instr2)
                    {
                        return true;
                    }

                    if (instr1 != null && instr2 != null && instr1.Length == instr2.Length)
                    {
                        for (int i = 0, l = (int)instr1.Length; i < l; i++)
                        {
                            if (instr1.Buffer[i] != instr2.Buffer[i])
                            {
                                return false;
                            }
                        }
                        return true;
                    }
                }

                return false;
            }


            /// <summary>
            /// Returns reversed branch operation for the current block.
            /// If no reverse opcode can be obtained Nop is returned.
            /// </summary>
            private ILOpCode GetReversedBranchOp()
            {
                var result = RevBranchCode;

                if (result != ILOpCode.Nop)
                {
                    return result;
                }

                // For some instructions reverse can be unambiguously inferred, 
                // but in other cases it depends on whether it was a float or an integer operation.
                // (we do not know if _un means unsigned or unordered here)
                switch (this.BranchCode)
                {
                    case ILOpCode.Brfalse:
                    case ILOpCode.Brfalse_s:
                        result = ILOpCode.Brtrue;
                        break;
                    case ILOpCode.Brtrue:
                    case ILOpCode.Brtrue_s:
                        result = ILOpCode.Brfalse;
                        break;
                    case ILOpCode.Beq:
                    case ILOpCode.Beq_s:
                        result = ILOpCode.Bne_un;
                        break;
                    case ILOpCode.Bne_un:
                    case ILOpCode.Bne_un_s:
                        result = ILOpCode.Beq;
                        break;
                }

                return result;
            }

            public virtual int TotalSize
            {
                get
                {
                    int branchSize;
                    switch (BranchCode)
                    {
                        case ILOpCode.Nop:
                            branchSize = 0; //Nop has 0 length here because we do not emit nop branch.
                            break;

                        case ILOpCode.Ret:
                        case ILOpCode.Throw:
                        case ILOpCode.Endfinally:
                            branchSize = 1;
                            break;

                        case ILOpCode.Rethrow:
                        case ILOpCode.Endfilter:
                            branchSize = 2;
                            break;

                        default:
                            Debug.Assert(BranchCode.Size() == 1);
                            branchSize = 1 + BranchCode.BranchOperandSize();
                            break;
                    }

                    return (int)RegularInstructionsLength + branchSize;
                }
            }

            private string GetDebuggerDisplay()
            {
#if DEBUG
                var visType = System.Type.GetType("Roslyn.Test.Utilities.ILBuilderVisualizer, Roslyn.Test.Utilities", false);
                if (visType != null)
                {
                    var method = visType.GetTypeInfo().GetDeclaredMethod("BasicBlockToString");
                    return (string)method.Invoke(this, SpecializedCollections.EmptyArray<object>());
                }
#endif

                return "";
            }

            private class PooledBasicBlock : BasicBlock
            {
                internal override void Free()
                {
                    base.Free();

                    this.branchLabel = null;
                    this.BranchCode = ILOpCode.Nop;
                    this.revBranchCode = 0;
                    this.NextBlock = null;
                    this.builder = null;
                    this.Reachability = Reachability.NotReachable;
                    this.Start = 0;

                    BasicBlock.Pool.Free(this);
                }
            }
        }

        internal class BasicBlockWithHandlerScope : BasicBlock
        {
            //nearest enclosing exception handler if any
            public readonly ExceptionHandlerScope enclosingHandler;

            public BasicBlockWithHandlerScope(ILBuilder builder, ExceptionHandlerScope enclosingHandler)
                : base(builder)
            {
                this.enclosingHandler = enclosingHandler;
            }

            public override ExceptionHandlerScope EnclosingHandler
            {
                get
                {
                    return enclosingHandler;
                }
            }
        }

        internal sealed class ExceptionHandlerLeaderBlock : BasicBlockWithHandlerScope
        {
            private readonly BlockType type;

            public ExceptionHandlerLeaderBlock(ILBuilder builder, ExceptionHandlerScope enclosingHandler, BlockType type) :
                base(builder, enclosingHandler)
            {
                this.type = type;
            }

            // The next exception handler clause (catch or finally)
            // in the same exception handler.
            public ExceptionHandlerLeaderBlock NextExceptionHandler;

            public override BlockType Type
            {
                get { return this.type; }
            }

            public override string ToString()
            {
                return string.Format("[{0}] {1}", this.type, base.ToString());
            }
        }

        // Basic block for the virtual switch instruction
        // Unlike a regular basic block, a switch block can
        // have more than one possible branch labels
        // and branch target blocks
        internal sealed class SwitchBlock : BasicBlockWithHandlerScope
        {
            public SwitchBlock(ILBuilder builder, ExceptionHandlerScope enclosingHandler) :
                base(builder, enclosingHandler)
            {
                this.SetBranchCode(ILOpCode.Switch);
            }

            public override BlockType Type
            {
                get { return BlockType.Switch; }
            }

            // destination labels for switch block
            public object[] BranchLabels;

            public uint BranchesCount
            {
                get
                {
                    Debug.Assert(BranchLabels != null);
                    return (uint)BranchLabels.Count();
                }
            }

            // get branch blocks for switch block
            public void GetBranchBlocks(ArrayBuilder<BasicBlock> branchBlocksBuilder)
            {
                // We need to regenerate the branch blocks array every time
                // as the labeled blocks might have been optimized.

                Debug.Assert(BranchesCount > 0);
                Debug.Assert(branchBlocksBuilder != null);

                foreach (var branchLabel in this.BranchLabels)
                {
                    branchBlocksBuilder.Add(builder.labelInfos[branchLabel].bb);
                }
            }

            public override int TotalSize
            {
                get
                {
                    // switch (N, t1, t2... tN)
                    //  IL ==> ILOpCode.Switch < unsigned int32 > < int32 >... < int32 > 

                    // size(ILOpCode.Switch) = 1
                    // size(N) = 4
                    // size(t1, t2,... tN) = 4*N

                    uint branchSize = 5 + 4 * this.BranchesCount;
                    return (int)(RegularInstructionsLength + branchSize);
                }
            }
        }
    }
}
