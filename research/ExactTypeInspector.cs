using System;
using System.IO;
using System.Reflection;

internal static class ExactTypeInspector
{
    static string directory;

    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string candidate = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
        return File.Exists(candidate) ? Assembly.ReflectionOnlyLoadFrom(candidate) : null;
    }

    static string TypeName(Type type)
    {
        if (type == null) return "<null>";
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        Type generic = type.GetGenericTypeDefinition();
        Type[] arguments = type.GetGenericArguments();
        string text = generic.FullName ?? generic.Name;
        int tick = text.IndexOf('`');
        if (tick >= 0) text = text.Substring(0, tick);
        text += "<";
        for (int i = 0; i < arguments.Length; i++) text += (i == 0 ? "" : ",") + TypeName(arguments[i]);
        return text + ">";
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: ExactTypeInspector ASSEMBLY TYPE..."); return 2; }
        string input = Path.GetFullPath(args[0]);
        directory = Path.GetDirectoryName(input);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += Resolve;
        Assembly assembly = Assembly.ReflectionOnlyLoadFrom(input);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        for (int i = 1; i < args.Length; i++) {
            Type type = assembly.GetType(args[i], false);
            if (type == null) { Console.WriteLine("MISSING " + args[i]); continue; }
            Console.WriteLine("TYPE " + type.FullName + " : " + (type.BaseType == null ? "" : type.BaseType.FullName));
            if (type.IsEnum) foreach (FieldInfo enumField in type.GetFields(BindingFlags.Public | BindingFlags.Static)) Console.WriteLine("  ENUM " + enumField.Name + " = " + enumField.GetRawConstantValue());
            foreach (FieldInfo field in type.GetFields(flags)) Console.WriteLine("  FIELD " + TypeName(field.FieldType) + " " + field.Name);
            foreach (PropertyInfo property in type.GetProperties(flags)) Console.WriteLine("  PROPERTY " + TypeName(property.PropertyType) + " " + property.Name);
            foreach (MethodInfo method in type.GetMethods(flags)) {
                string parameters = "";
                ParameterInfo[] list = method.GetParameters();
                for (int p = 0; p < list.Length; p++) parameters += (p == 0 ? "" : ", ") + TypeName(list[p].ParameterType) + " " + list[p].Name;
                Console.WriteLine("  METHOD " + TypeName(method.ReturnType) + " " + method.Name + "(" + parameters + ")");
            }
        }
        return 0;
    }
}
