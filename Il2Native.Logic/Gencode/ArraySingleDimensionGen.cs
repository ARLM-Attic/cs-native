﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ArraySingleDimensionGen.cs" company="">
//   
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------
namespace Il2Native.Logic.Gencode
{
    using Il2Native.Logic.CodeParts;

    using PEAssemblyReader;

    /// <summary>
    /// </summary>
    public static class ArraySingleDimensionGen
    {
        /// <summary>
        /// </summary>
        /// <param name="llvmWriter">
        /// </param>
        /// <param name="opCode">
        /// </param>
        public static void WriteArrayGetLength(this LlvmWriter llvmWriter, OpCodePart opCode)
        {
            var writer = llvmWriter.Output;

            var typeToLoad = llvmWriter.ResolveType("System.Int32");
            llvmWriter.WriteBitcast(opCode, opCode.OpCodeOperands[0].Result, typeToLoad);
            writer.WriteLine(string.Empty);

            var res = opCode.Result;
            var resLen = llvmWriter.WriteSetResultNumber(opCode, typeToLoad);
            writer.Write("getelementptr ");
            typeToLoad.WriteTypePrefix(writer);
            writer.Write("* ");
            llvmWriter.WriteResultNumber(res);
            writer.WriteLine(", i32 -1");

            opCode.Result = null;
            llvmWriter.WriteLlvmLoad(opCode, typeToLoad, resLen);
        }

        /// <summary>
        /// </summary>
        /// <param name="llvmWriter">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="declaringType">
        /// </param>
        /// <param name="length">
        /// </param>
        public static void WriteNewArray(this LlvmWriter llvmWriter, OpCodePart opCode, IType declaringType, OpCodePart length)
        {
            if (opCode.HasResult)
            {
                return;
            }

            var writer = llvmWriter.Output;

            writer.WriteLine("; New array");

            var size = declaringType.GetTypeSize();
            llvmWriter.UnaryOper(writer, opCode, "mul");
            writer.WriteLine(", {0}", size);

            var resMul = opCode.Result;

            var intType = llvmWriter.ResolveType("System.Int32");
            llvmWriter.WriteSetResultNumber(opCode, intType);
            writer.Write("add i32 4, {0}", resMul);
            writer.WriteLine(string.Empty);

            var resAdd = opCode.Result;

            var resAlloc = llvmWriter.WriteSetResultNumber(opCode, llvmWriter.ResolveType("System.Byte").ToPointerType());
            writer.Write("call i8* @malloc(i32 {0})", resAdd);
            writer.WriteLine(string.Empty);

            llvmWriter.WriteBitcast(opCode, resAlloc, intType);
            writer.WriteLine(string.Empty);

            var opCodeTemp = OpCodePart.CreateNop;
            opCodeTemp.OpCodeOperands = opCode.OpCodeOperands;

            // save array size
            llvmWriter.ProcessOperator(writer, opCodeTemp, "store");
            llvmWriter.PostProcessOperand(writer, opCode, 0, !opCode.OpCodeOperands[0].HasResult);
            writer.Write(", ");
            intType.WriteTypePrefix(writer);
            writer.Write("* ");
            llvmWriter.WriteResultNumber(opCode.Result);
            writer.WriteLine(string.Empty);

            var tempRes = opCode.Result;
            var resGetArr = llvmWriter.WriteSetResultNumber(opCode, intType);
            writer.Write("getelementptr ");

            // WriteTypePrefix(writer, declaringType);
            writer.Write("i32* ");
            llvmWriter.WriteResultNumber(tempRes);
            writer.WriteLine(", i32 1");

            if (declaringType.TypeNotEquals(intType))
            {
                llvmWriter.WriteCast(opCode, resGetArr, declaringType, !declaringType.IsValueType);
                writer.WriteLine(string.Empty);
            }

            opCode.Result = new LlvmResult(opCode.Result.Number, declaringType.ToArrayType(1));

            writer.WriteLine("; end of new array");
        }

        public static bool IsItArrayInitialization(this IMethod methodBase)
        {
            if (methodBase.Name == "InitializeArray"
                && methodBase.Namespace == "System.Runtime.CompilerServices")
            {
                return true;
            }

            return false;
        }

        public static void WriteArrayInit(this LlvmWriter llvmWriter, OpCodePart opCode)
        {
            var writer = llvmWriter.Output;

            writer.WriteLine("; Init array with values");

            var opCodeFieldInfoPart = opCode.OpCodeOperands[1] as OpCodeFieldInfoPart;
            var data = opCodeFieldInfoPart.Operand.GetFieldRVAData();

            var arrayIndex = llvmWriter.GetArrayIndex(data);
            var arrayLength = int.Parse(opCodeFieldInfoPart.Operand.FieldType.MetadataName.Substring("__StaticArrayInitTypeSize=".Length));
            var arrayData = string.Format(
                "bitcast ([{1} x i8]* getelementptr inbounds ({2} i32, [{1} x i8] {3}* @.array{0}, i32 0, i32 1) to i8*)",
                arrayIndex,
                data.Length,
                '{',
                '}');

            writer.WriteLine(
                "call void @llvm.memcpy.p0i8.p0i8.i32(i8* {0}, i8* {1}, i32 {2}, i32 {3}, i1 false)",
                opCode.OpCodeOperands[0].Result,
                arrayData,
                arrayLength,
                LlvmWriter.PointerSize/*Align*/);

            writer.WriteLine(string.Empty);
        }
    }
}