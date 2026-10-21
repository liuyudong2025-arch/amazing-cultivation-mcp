using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

internal static class CallerInspector
{
    static string directory;
    static readonly Dictionary<short, OpCode> Ops = new Dictionary<short, OpCode>();
    static CallerInspector() { foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) { OpCode op = (OpCode)f.GetValue(null); Ops[op.Value] = op; } }
    static Assembly Resolve(object sender, ResolveEventArgs args) { string p = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll"); return File.Exists(p) ? Assembly.ReflectionOnlyLoadFrom(p) : null; }
    static int Size(OperandType type, byte[] il, ref int p) {
        switch (type) {
            case OperandType.InlineNone: return 0; case OperandType.ShortInlineI: case OperandType.ShortInlineVar: case OperandType.ShortInlineBrTarget: return 1;
            case OperandType.InlineVar: return 2; case OperandType.InlineI: case OperandType.ShortInlineR: case OperandType.InlineBrTarget: case OperandType.InlineString: case OperandType.InlineField: case OperandType.InlineType: case OperandType.InlineTok: case OperandType.InlineSig: return 4;
            case OperandType.InlineI8: case OperandType.InlineR: return 8; case OperandType.InlineSwitch: int n = BitConverter.ToInt32(il, p); return 4 + n * 4; default: return 4;
        }
    }
    public static int Main(string[] args) {
        if (args.Length < 4) { Console.Error.WriteLine("Usage: CallerInspector ASSEMBLY DECLARING_TYPE METHOD [PARAM_COUNT]"); return 2; }
        string input = Path.GetFullPath(args[0]); directory = Path.GetDirectoryName(input); AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += Resolve;
        Assembly asm = Assembly.ReflectionOnlyLoadFrom(input); Type owner = asm.GetType(args[1], true); int paramCount = args.Length > 3 ? Int32.Parse(args[3]) : -1;
        MethodInfo target = null; foreach (MethodInfo m in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) if (m.Name == args[2] && (paramCount < 0 || m.GetParameters().Length == paramCount)) { target = m; break; }
        if (target == null) return 3;
        foreach (Type type in asm.GetTypes()) foreach (MethodBase method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            MethodBody body; try { body = method.GetMethodBody(); } catch { continue; } if (body == null) continue; byte[] il = body.GetILAsByteArray(); int p = 0;
            while (p < il.Length) { short key = il[p++]; if (key == 0xfe) key = (short)(0xfe00 | il[p++]); OpCode op; if (!Ops.TryGetValue(key, out op)) break;
                if (op.OperandType == OperandType.InlineMethod) { int token = BitConverter.ToInt32(il, p); try { MethodBase called = method.Module.ResolveMethod(token); if (called.MetadataToken == target.MetadataToken && called.Module == target.Module) { Console.WriteLine(type.FullName + "::" + method); break; } } catch { } }
                p += Size(op.OperandType, il, ref p);
            }
        }
        return 0;
    }
}
