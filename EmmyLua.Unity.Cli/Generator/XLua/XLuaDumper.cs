using System.Text;
using Microsoft.CodeAnalysis;

namespace EmmyLua.Unity.Generator.XLua;

public class XLuaDumper : IDumper
{
    public string Name => "XLuaDumper";

    // 500kb
    private static readonly int SingleFileLength = 500 * 1024;

    private int Count { get; set; } = 0;

    private Dictionary<string, bool> NamespaceDict { get; } = new();

    // 类型引用追踪器
    private TypeReferenceTracker TypeTracker { get; } = new();

    public void Dump(List<CSType> csTypes, string outPath)
    {
        try
        {
            if (!Directory.Exists(outPath)) Directory.CreateDirectory(outPath);

            // 合并泛型类型
            var genericManager = new GenericTypeManager();
            csTypes = genericManager.ProcessTypes(csTypes);

            // 第一遍：收集所有已导出的类型和命名空间
            TypeTracker.CollectExportedTypes(csTypes);
            
            // 预收集所有命名空间，确保在生成文件前NamespaceDict已被填充
            foreach (var csType in csTypes)
            {
                switch (csType)
                {
                    case CSClassType csClassType:
                        RegisterNamespace(csClassType.Namespace, csClassType.Name);
                        break;
                    case CSInterface csInterface:
                        RegisterNamespace(csInterface.Namespace, csInterface.Name);
                        break;
                    case CSEnumType csEnumType:
                        RegisterNamespace(csEnumType.Namespace, csEnumType.Name);
                        break;
                    case CSDelegate csDelegate:
                        RegisterNamespace(csDelegate.Namespace, csDelegate.Name);
                        break;
                }
            }

            var sb = new StringBuilder();
            ResetSb(sb);

            // 第二遍：导出类型并收集未导出的引用类型
            foreach (var csType in csTypes)
                try
                {
                    switch (csType)
                    {
                        case CSClassType csClassType:
                            HandleCsClassType(csClassType, sb);
                            break;
                        case CSInterface csInterface:
                            HandleCsInterface(csInterface, sb);
                            break;
                        case CSEnumType csEnumType:
                            HandleCsEnumType(csEnumType, sb);
                            break;
                        case CSDelegate csDelegate:
                            HandleCsDelegate(csDelegate, sb);
                            break;
                    }

                    sb.AppendLine();
                    CacheOrDumpToFile(sb, outPath);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error dumping type '{csType.Name}': {e.Message}");
                }

            if (sb.Length > 0) CacheOrDumpToFile(sb, outPath, true);

            TypeTracker.DumpUnexportedTypes(outPath, "xlua_noexport_types.lua");

            Console.WriteLine($"Successfully generated {Count} Lua definition files.");
            Console.WriteLine(
                $"Found {TypeTracker.GetUnexportedTypeCount()} unexported types referenced in exported types.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Fatal error during dump: {e.Message}");
            throw;
        }
    }

    private void DumpNamespace(StringBuilder sb, string outPath)
    {
        sb.AppendLine("CS = {}");
        foreach (var (namespaceString, isNamespace) in NamespaceDict)
            if (isNamespace)
                sb.AppendLine($"---@type namespace <\"{namespaceString}\">\nCS.{namespaceString} = {{}}");
            else
                sb.AppendLine($"---@type {namespaceString}\nCS.{namespaceString} = {{}}");

        // 添加 XLua 的 typeof 函数定义
        sb.AppendLine();
        sb.AppendLine("---XLua typeof 函数，用于获取 C# 类型");
        sb.AppendLine("---@param type any");
        sb.AppendLine("---@return System.Type");
        sb.AppendLine("function typeof(type) end");

        var filePath = Path.Combine(outPath, "xlua_namespace.lua");
        File.WriteAllText(filePath, sb.ToString());
    }

    private void CacheOrDumpToFile(StringBuilder sb, string outPath, bool force = false)
    {
        if (sb.Length > SingleFileLength || force)
            try
            {
                var filePath = Path.Combine(outPath, $"xlua_dump_{Count}.lua");
                File.WriteAllText(filePath, sb.ToString());
                ResetSb(sb);
                Count++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error writing file: {e.Message}");
                throw;
            }
    }

    private void ResetSb(StringBuilder sb)
    {
        sb.Clear();
        sb.AppendLine("---@meta");
        sb.AppendLine();
        sb.AppendLine("CS = {}");
        
        // 添加 XLua 的 typeof 函数定义
        sb.AppendLine();
        sb.AppendLine("---XLua typeof 函数，用于获取 C# 类型");
        sb.AppendLine("---@param type any");
        sb.AppendLine("---@return System.Type");
        sb.AppendLine("function typeof(type) end");
        sb.AppendLine();
    }

    /// <summary>
    /// 在生成类型定义之前，确保所有必要的命名空间都已创建
    /// </summary>
    private void EnsureNamespaceExists(StringBuilder sb, string namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
            return;
        
        var namespaces = namespaceName.Split('.');
        var currentNamespace = "CS";
        
        for (int i = 0; i < namespaces.Length; i++)
        {
            currentNamespace += "." + namespaces[i];
            sb.AppendLine(currentNamespace + " = " + currentNamespace + " or {}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// 检查是否为基本类型
    /// </summary>
    private bool IsPrimitiveType(string typeName)
    {
        var primitiveTypes = new[]
        {
            "void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint",
            "long", "ulong", "float", "double", "decimal", "char", "string", "object",
            "boolean", "integer", "number", "any"
        };
        return primitiveTypes.Contains(typeName);
    }

    private void HandleCsClassType(CSClassType csClassType, StringBuilder sb)
    {
        RegisterNamespace(csClassType.Namespace, csClassType.Name);

        var classFullName = GetFullTypeName(csClassType.Namespace, csClassType.Name);

        // 检查基类和接口
        TypeTracker.CheckAndRecordType(csClassType.BaseClass);
        foreach (var iface in csClassType.Interfaces) TypeTracker.CheckAndRecordType(iface);

        // 确保命名空间存在
        EnsureNamespaceExists(sb, csClassType.Namespace);
        
        LuaAnnotationFormatter.WriteCommentAndLocation(sb, csClassType.Comment, csClassType.Location);
        LuaAnnotationFormatter.WriteTypeAnnotation(
            sb, "class", classFullName, csClassType.BaseClass, csClassType.Interfaces, csClassType.GenericTypes);

        // Write constructor overloads
        if (!csClassType.IsStatic)
        {
            var ctors = GetCtorList(csClassType);
            if (ctors.Count > 0)
                foreach (var ctor in ctors)
                {
                    // 检查构造函数参数类型
                    foreach (var param in ctor.Params) TypeTracker.CheckAndRecordType(param.TypeName);
                    LuaAnnotationFormatter.WriteConstructorOverload(sb, ctor, classFullName);
                }
            else
                sb.AppendLine($"---@overload fun(): {classFullName}");
        }

        // 直接使用完整CS路径定义类
        var fullCsPath = !string.IsNullOrEmpty(csClassType.Namespace)
            ? $"CS.{csClassType.Namespace}.{csClassType.Name}"
            : $"CS.{csClassType.Name}";
        sb.AppendLine($"{fullCsPath} = {{}}");

        // 定义所有字段，确保IDE能正确识别类型
        foreach (var field in csClassType.Fields)
        {
            // 检查字段类型
            TypeTracker.CheckAndRecordType(field.TypeName);

            LuaAnnotationFormatter.WriteCommentAndLocation(sb, field.Comment, field.Location);

            // 确保类型名带有CS前缀
            var typeName = field.TypeName;
            // 跳过基本类型和已带有CS前缀的类型
            if (!string.IsNullOrEmpty(typeName) && !typeName.StartsWith("CS.") && 
                !typeName.StartsWith("System.") && !IsPrimitiveType(typeName))
            {
                // 检查是否包含命名空间
                if (typeName.Contains('.'))
                {
                    typeName = "CS." + typeName;
                }
                else
                {
                    // 对于没有命名空间的类型，检查是否为已知的Unity或其他框架类型
                    typeName = "CS." + typeName;
                }
            }

            // 直接使用完整CS路径定义字段
            sb.AppendLine($"---@type {typeName}");
            
            // 对于静态单例属性inst，使用更合适的初始化值以帮助IDE类型推断
            if (field.Name == "inst")
            {
                sb.AppendLine($"{fullCsPath}.{field.Name} = {fullCsPath}");
            }
            else
            {
                sb.AppendLine($"{fullCsPath}.{field.Name} = nil");
            }
            sb.AppendLine();
        }

        // 最后定义所有方法，确保IDE能正确关联方法到类
        foreach (var method in csClassType.Methods)
        {
            if (method.Name == ".ctor")
                continue;

            // 检查返回类型
            TypeTracker.CheckAndRecordType(method.ReturnTypeName);

            // 检查参数类型
            foreach (var param in method.Params) TypeTracker.CheckAndRecordType(param.TypeName);

            LuaAnnotationFormatter.WriteCommentAndLocation(sb, method.Comment, method.Location);
            var outParams = LuaAnnotationFormatter.WriteParameterAnnotations(sb, method.Params);
            LuaAnnotationFormatter.WriteReturnAnnotation(sb, method.ReturnTypeName, outParams);
            
            // 直接使用完整CS路径定义方法（传统函数格式）
            var paramNames = string.Join(", ", method.Params.Select(p => p.Name));
            sb.AppendLine($"function {fullCsPath}:{method.Name}({paramNames})");
            sb.AppendLine("end");
            sb.AppendLine();
        }
    }

    private void RegisterNamespace(string namespaceName, string typeName)
    {
        if (!string.IsNullOrEmpty(namespaceName))
        {
            // 注册完整的命名空间路径
            var namespaces = namespaceName.Split('.');
            for (int i = 1; i <= namespaces.Length; i++)
            {
                var currentNamespace = string.Join(".", namespaces.Take(i));
                NamespaceDict.TryAdd(currentNamespace, true);
            }
        }
        else
        {
            NamespaceDict.TryAdd(typeName, false);
        }
    }

    private static string GetFullTypeName(string namespaceName, string typeName)
    {
        return !string.IsNullOrEmpty(namespaceName)
            ? $"CS.{namespaceName}.{typeName}"
            : $"CS.{typeName}";
    }

    private void HandleCsInterface(CSInterface csInterface, StringBuilder sb)
    {
        RegisterNamespace(csInterface.Namespace, csInterface.Name);

        var interfaceFullName = GetFullTypeName(csInterface.Namespace, csInterface.Name);

        // 确保命名空间存在
        EnsureNamespaceExists(sb, csInterface.Namespace);
        
        // 检查接口继承
        foreach (var iface in csInterface.Interfaces) TypeTracker.CheckAndRecordType(iface);

        // 检查字段类型
        foreach (var field in csInterface.Fields) TypeTracker.CheckAndRecordType(field.TypeName);

        // 检查方法类型
        foreach (var method in csInterface.Methods)
        {
            TypeTracker.CheckAndRecordType(method.ReturnTypeName);
            foreach (var param in method.Params) TypeTracker.CheckAndRecordType(param.TypeName);
        }

        sb.AppendLine($"---@interface {interfaceFullName}");
        
        // 直接使用完整CS路径定义接口
        var fullCsPath = !string.IsNullOrEmpty(csInterface.Namespace)
            ? $"CS.{csInterface.Namespace}.{csInterface.Name}"
            : $"CS.{csInterface.Name}";
        sb.AppendLine($"{fullCsPath} = {{}}");
        sb.AppendLine();
    }

    private void HandleCsEnumType(CSEnumType csEnumType, StringBuilder sb)
    {
        RegisterNamespace(csEnumType.Namespace, csEnumType.Name);

        var classFullName = GetFullTypeName(csEnumType.Namespace, csEnumType.Name);

        // 确保命名空间存在
        EnsureNamespaceExists(sb, csEnumType.Namespace);
        
        LuaAnnotationFormatter.WriteCommentAndLocation(sb, csEnumType.Comment, csEnumType.Location);
        LuaAnnotationFormatter.WriteTypeAnnotation(sb, "enum", classFullName);

        // 直接使用完整CS路径定义枚举
        var fullCsPath = !string.IsNullOrEmpty(csEnumType.Namespace)
            ? $"CS.{csEnumType.Namespace}.{csEnumType.Name}"
            : $"CS.{csEnumType.Name}";
        sb.AppendLine($"{fullCsPath} = {{");

        foreach (var field in csEnumType.Fields)
        {
            LuaAnnotationFormatter.WriteCommentAndLocation(sb, field.Comment, field.Location, 4);
            // 使用实际的枚举值，如果没有则默认为 0
            var enumValue = field.ConstantValue ?? 0;
            sb.AppendLine($"    {field.Name} = {enumValue},");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void HandleCsDelegate(CSDelegate csDelegate, StringBuilder sb)
    {
        RegisterNamespace(csDelegate.Namespace, csDelegate.Name);

        // 确保命名空间存在
        EnsureNamespaceExists(sb, csDelegate.Namespace);
        
        // 检查委托的返回类型和参数类型
        TypeTracker.CheckAndRecordType(csDelegate.InvokeMethod.ReturnTypeName);
        foreach (var param in csDelegate.InvokeMethod.Params) TypeTracker.CheckAndRecordType(param.TypeName);

        // 使用完整的委托名称（带有CS前缀和命名空间）
        var fullDelegateName = GetFullTypeName(csDelegate.Namespace, csDelegate.Name);
        LuaAnnotationFormatter.WriteDelegateAlias(sb, fullDelegateName, csDelegate.InvokeMethod);
    }

    private List<CSTypeMethod> GetCtorList(CSClassType csClassType)
    {
        return csClassType.Methods.FindAll(method => method.Name == ".ctor");
    }
}