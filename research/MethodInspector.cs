using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

internal static class MethodInspector
{
    static string directory;
    static readonly Dictionary<short, OpCode> Ops = new Dictionary<short, OpCode>();

    static MethodInspector()
    {
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            if (f.FieldType == typeof(OpCode)) { OpCode op = (OpCode)f.GetValue(null); Ops[op.Value] = op; }
        }
    }

    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string candidate = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
        return File.Exists(candidate) ? Assembly.ReflectionOnlyLoadFrom(candidate) : null;
    }

    static int I4(byte[] b, ref int p) { int v = BitConverter.ToInt32(b, p); p += 4; return v; }

    public static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("Usage: MethodInspector ASSEMBLY TYPE METHOD"); return 2; }
        string input = Path.GetFullPath(args[0]); directory = Path.GetDirectoryName(input);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += Resolve;
        Assembly asm = Assembly.ReflectionOnlyLoadFrom(input);
        Type type = asm.GetType(args[1], true);
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            if (method.Name != args[2]) continue;
            Console.WriteLine("METHOD " + method);
            MethodBody body = method.GetMethodBody(); if (body == null) continue;
            byte[] il = body.GetILAsByteArray(); int p = 0;
            while (p < il.Length) {
                int at = p; short key = il[p++]; if (key == 0xfe) key = (short)(0xfe00 | il[p++]);
                OpCode op; if (!Ops.TryGetValue(key, out op)) { Console.WriteLine(at.ToString("X4") + " ?"); break; }
                object operand = null;
                try {
                    switch (op.OperandType) {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineI: operand = il[p++]; break;
                        case OperandType.InlineI: operand = I4(il, ref p); break;
                        case OperandType.InlineI8: operand = BitConverter.ToInt64(il, p); p += 8; break;
                        case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, p); p += 4; break;
                        case OperandType.InlineR: operand = BitConverter.ToDouble(il, p); p += 8; break;
                        case OperandType.ShortInlineVar: operand = il[p++]; break;
                        case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, p); p += 2; break;
                        case OperandType.ShortInlineBrTarget: operand = p + 1 + (sbyte)il[p]; p++; break;
                        case OperandType.InlineBrTarget: { int d = I4(il, ref p); operand = p + d; break; }
                        case OperandType.InlineSwitch: { int n = I4(il, ref p); p += n * 4; operand = "switch[" + n + "]"; break; }
                        default:
                            int token = I4(il, ref p);
                            try {
                                if (op.OperandType == OperandType.InlineString) operand = "\"" + method.Module.ResolveString(token) + "\"";
                                else if (op.OperandType == OperandType.InlineMethod) operand = method.Module.ResolveMethod(token);
                                else if (op.OperandType == OperandType.InlineField) operand = method.Module.ResolveField(token);
                                else if (op.OperandType == OperandType.InlineType) operand = method.Module.ResolveType(token);
                                else operand = "token 0x" + token.ToString("X8");
                            } catch { operand = "token 0x" + token.ToString("X8"); }
                            break;
                    }
                } catch (Exception ex) { operand = "<" + ex.Message + ">"; }
                Console.WriteLine(at.ToString("X4") + " " + op.Name + (operand == null ? "" : " " + operand));
            }
        }
        return 0;
    }
}
