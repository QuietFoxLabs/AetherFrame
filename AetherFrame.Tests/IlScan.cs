using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace AetherFrame.Tests;

/// <summary>
/// Walks a method body's IL and reports every instruction whose operand is a metadata token, with
/// that token. It knows the operand size of every standard opcode (ECMA-335, partition III), so it
/// never loses its place, and an opcode it doesn't know stops the walk with an exception rather
/// than a guess.
/// </summary>
internal static class IlScan
{
    private const int NoPrefix = 0xFE19;

    private enum Operand
    {
        None,
        One,
        Two,
        Four,
        Eight,
        Token,
        Switch,
    }

    internal static List<(ILOpCode OpCode, int Token)> TokenOperands(BlobReader reader)
    {
        var found = new List<(ILOpCode OpCode, int Token)>();
        while (reader.RemainingBytes > 0)
        {
            var first = reader.ReadByte();
            var opCode = first == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)first;
            switch (OperandOf(opCode))
            {
                case Operand.One:
                    reader.Offset += 1;
                    break;
                case Operand.Two:
                    reader.Offset += 2;
                    break;
                case Operand.Four:
                    reader.Offset += 4;
                    break;
                case Operand.Eight:
                    reader.Offset += 8;
                    break;
                case Operand.Token:
                    found.Add((opCode, reader.ReadInt32()));
                    break;
                case Operand.Switch:
                    var targets = reader.ReadInt32();
                    reader.Offset += 4 * targets;
                    break;
            }
        }

        return found;
    }

    /// <summary>Every instruction in order, with its token operand, or 0 for one without.</summary>
    internal static List<(ILOpCode OpCode, int Token)> Instructions(BlobReader reader)
    {
        var found = new List<(ILOpCode OpCode, int Token)>();
        while (reader.RemainingBytes > 0)
        {
            var first = reader.ReadByte();
            var opCode = first == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)first;
            var token = 0;
            switch (OperandOf(opCode))
            {
                case Operand.One:
                    reader.Offset += 1;
                    break;
                case Operand.Two:
                    reader.Offset += 2;
                    break;
                case Operand.Four:
                    reader.Offset += 4;
                    break;
                case Operand.Eight:
                    reader.Offset += 8;
                    break;
                case Operand.Token:
                    token = reader.ReadInt32();
                    break;
                case Operand.Switch:
                    var targets = reader.ReadInt32();
                    reader.Offset += 4 * targets;
                    break;
            }

            found.Add((opCode, token));
        }

        return found;
    }

    /// <summary>
    /// Every instruction in order, with its token operand (0 for one without), and the integer
    /// constant it loads when it is one of the <c>ldc.i4</c> forms (null otherwise).
    /// </summary>
    internal static List<(ILOpCode OpCode, int Token, int? Constant)> InstructionsWithConstants(BlobReader reader)
    {
        var found = new List<(ILOpCode OpCode, int Token, int? Constant)>();
        while (reader.RemainingBytes > 0)
        {
            var first = reader.ReadByte();
            var opCode = first == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)first;
            var token = 0;
            int? constant = opCode switch
            {
                >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8 => (int)opCode - (int)ILOpCode.Ldc_i4_0,
                _ => null,
            };
            switch (OperandOf(opCode))
            {
                case Operand.One:
                    var small = reader.ReadSByte();
                    if (opCode == ILOpCode.Ldc_i4_s)
                    {
                        constant = small;
                    }

                    break;
                case Operand.Two:
                    reader.Offset += 2;
                    break;
                case Operand.Four:
                    var four = reader.ReadInt32();
                    if (opCode == ILOpCode.Ldc_i4)
                    {
                        constant = four;
                    }

                    break;
                case Operand.Eight:
                    reader.Offset += 8;
                    break;
                case Operand.Token:
                    token = reader.ReadInt32();
                    break;
                case Operand.Switch:
                    var targets = reader.ReadInt32();
                    reader.Offset += 4 * targets;
                    break;
            }

            found.Add((opCode, token, constant));
        }

        return found;
    }

    private static Operand OperandOf(ILOpCode opCode)
    {
        switch (opCode)
        {
            case ILOpCode.Ldarg_s:
            case ILOpCode.Ldarga_s:
            case ILOpCode.Starg_s:
            case ILOpCode.Ldloc_s:
            case ILOpCode.Ldloca_s:
            case ILOpCode.Stloc_s:
            case ILOpCode.Ldc_i4_s:
            case ILOpCode.Unaligned:
                return Operand.One;
            case ILOpCode.Ldarg:
            case ILOpCode.Ldarga:
            case ILOpCode.Starg:
            case ILOpCode.Ldloc:
            case ILOpCode.Ldloca:
            case ILOpCode.Stloc:
                return Operand.Two;
            case ILOpCode.Ldc_i4:
            case ILOpCode.Ldc_r4:
                return Operand.Four;
            case ILOpCode.Ldc_i8:
            case ILOpCode.Ldc_r8:
                return Operand.Eight;
            case ILOpCode.Switch:
                return Operand.Switch;
            case ILOpCode.Jmp:
            case ILOpCode.Call:
            case ILOpCode.Calli:
            case ILOpCode.Callvirt:
            case ILOpCode.Cpobj:
            case ILOpCode.Ldobj:
            case ILOpCode.Ldstr:
            case ILOpCode.Newobj:
            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Unbox:
            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
            case ILOpCode.Stfld:
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
            case ILOpCode.Stsfld:
            case ILOpCode.Stobj:
            case ILOpCode.Box:
            case ILOpCode.Newarr:
            case ILOpCode.Ldelema:
            case ILOpCode.Ldelem:
            case ILOpCode.Stelem:
            case ILOpCode.Unbox_any:
            case ILOpCode.Refanyval:
            case ILOpCode.Mkrefany:
            case ILOpCode.Ldtoken:
            case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
            case ILOpCode.Initobj:
            case ILOpCode.Constrained:
            case ILOpCode.Sizeof:
                return Operand.Token;
        }

        if (opCode.IsBranch())
        {
            return opCode.GetBranchOperandSize() == 1 ? Operand.One : Operand.Four;
        }

        if ((int)opCode == NoPrefix)
        {
            return Operand.One;
        }

        return Enum.IsDefined(opCode)
            ? Operand.None
            : throw new InvalidOperationException($"IL opcode 0x{(int)opCode:X} is not one this scan knows.");
    }
}
