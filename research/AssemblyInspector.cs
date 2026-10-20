using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

internal static class AssemblyInspector
{
    private static string directory;

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string candidate = Path.Combine(directory, name);
        return File.Exists(candidate) ? Assembly.ReflectionOnlyLoadFrom(candidate) : null;
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: AssemblyInspector ASSEMBLY REGEX");
            return 2;
        }
        string input = Path.GetFullPath(args[0]);
        directory = Path.GetDirectoryName(input);
        Regex filter = new Regex(args[1], RegexOptions.IgnoreCase);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += Resolve;
        Assembly assembly = Assembly.ReflectionOnlyLoadFrom(input);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        foreach (Type type in types) {
            if (type == null || !filter.IsMatch(type.FullName)) continue;
            Console.WriteLine("TYPE " + type.FullName + " : " + (type.BaseType == null ? "" : type.BaseType.FullName));
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(flags))
                Console.WriteLine("  FIELD " + field.FieldType.FullName + " " + field.Name);
            foreach (PropertyInfo property in type.GetProperties(flags))
                Console.WriteLine("  PROPERTY " + property.PropertyType.FullName + " " + property.Name);
            foreach (MethodInfo method in type.GetMethods(flags)) {
                ParameterInfo[] parameters = method.GetParameters();
                string signature = "";
                for (int i = 0; i < parameters.Length; i++) {
                    if (i > 0) signature += ", ";
                    signature += parameters[i].ParameterType.FullName + " " + parameters[i].Name;
                }
                Console.WriteLine("  METHOD " + method.ReturnType.FullName + " " + method.Name + "(" + signature + ")");
            }
        }
        return 0;
    }
}
