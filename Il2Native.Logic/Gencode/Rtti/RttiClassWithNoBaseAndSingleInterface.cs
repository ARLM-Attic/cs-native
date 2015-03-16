﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="RttiClassWithNoBaseAndSingleInterface.cs" company="">
//   
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Il2Native.Logic.Gencode
{
    using System.CodeDom.Compiler;
    using System.Linq;
    using PEAssemblyReader;

    /// <summary>
    /// </summary>
    public class RttiClassWithNoBaseAndSingleInterface
    {
        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="writer">
        /// </param>
        public static void WriteRttiClassInfoDeclaration(IType type, IndentedTextWriter writer)
        {
            writer.Write("{ Byte* f1; Byte* f2; Byte* f3; }");
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="cWriter">
        /// </param>
        public static void WriteRttiClassInfoDefinition(IType type, CWriter cWriter)
        {
            var writer = cWriter.Output;

            writer.WriteLine("{");
            writer.Indent++;
            writer.WriteLine(
                "(Byte*) (((Byte**) &_ZTVN10__cxxabiv120__si_class_type_infoE) + 2),");
            writer.Write("(Byte*)");
            type.WriteRttiClassNameString(writer);
            var singleInheritanceType = type.GetInterfaces().First();
            writer.Write(",(Byte*)&");
            writer.Write(singleInheritanceType.GetRttiInfoName());
            writer.Indent--;
            writer.Write("}");
        }
    }
}