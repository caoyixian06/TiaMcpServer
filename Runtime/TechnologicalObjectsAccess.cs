using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace TiaMcpServer
{
    /// <summary>
    /// 工艺对象 (TechnologicalObject) 访问兼容层。
    /// V19: PlcSoftware.TechnologicalObjectGroup 类型为 TechnologicalObjectGroup，元素为 TechnologicalObject。
    /// V17: 同属性名但类型为 TechnologicalInstanceDBGroup，元素为 TechnologicalInstanceDB。
    /// 两者均暴露 TechnologicalObjects 组合与 Name/Number/OfSystemLibElement/OfSystemLibVersion/Parameters，
    /// 因此通过反射统一访问，避免跨版本编译失败。V17 不支持创建轴类工艺对象，Create 失败会给出明确提示。
    /// </summary>
    internal static class TechnologicalObjectsAccess
    {
        /// <summary>反射获取 PlcSoftware.TechnologicalObjectGroup.TechnologicalObjects 组合对象。</summary>
        public static object? GetComposition(PlcSoftware plc)
        {
            if (plc == null) return null;
            var group = Get(plc, "TechnologicalObjectGroup");
            return Get(group, "TechnologicalObjects");
        }

        /// <summary>枚举组合中所有工艺对象。</summary>
        public static List<object?> Enumerate(object? composition)
        {
            var list = new List<object?>();
            if (composition == null || composition is not IEnumerable en) return list;
            foreach (var item in en) list.Add(item);
            return list;
        }

        /// <summary>按名称查找工艺对象。</summary>
        public static object? Find(object? composition, string name)
        {
            if (composition == null) return null;
            var m = composition.GetType().GetMethod("Find", new[] { typeof(string) });
            return m?.Invoke(composition, new object[] { name });
        }

        /// <summary>创建工艺对象（V17 无轴 TO 创建支持时抛异常）。</summary>
        public static object? Create(object? composition, string name, string type, Version version)
        {
            if (composition == null) return null;
            var m = composition.GetType().GetMethod("Create", new[] { typeof(string), typeof(string), typeof(Version) });
            return m?.Invoke(composition, new object[] { name, type, version });
        }

        /// <summary>导入工艺对象 XML。</summary>
        public static List<object?> Import(object? composition, FileInfo file, ImportOptions options)
        {
            var list = new List<object?>();
            if (composition == null) return list;
            var m = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
            var result = m?.Invoke(composition, new object[] { file, options });
            if (result is IEnumerable en)
                foreach (var item in en) list.Add(item);
            return list;
        }

        public static object? Get(object? target, string property)
        {
            try
            {
                if (target == null) return null;
                return target.GetType().GetProperty(property)?.GetValue(target, null);
            }
            catch (TargetInvocationException) { return null; }
        }

        public static string? GetString(object? target, string property) => Get(target, property)?.ToString();

        public static void Invoke(object? target, string method, params object[] args)
        {
            if (target == null) return;
            var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, args.Select(a => a.GetType()).ToArray(), null)
                ?? target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            m?.Invoke(target, args.Length > 0 ? args : null);
        }

        public static object? InvokeReturn(object? target, string method, params object[] args)
        {
            if (target == null) return null;
            var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            return m?.Invoke(target, args.Length > 0 ? args : null);
        }

        /// <summary>获取工艺对象参数集合（TechnologicalParameter 组合）。</summary>
        public static IEnumerable<object?> GetParameters(object? to)
        {
            var p = Get(to, "Parameters");
            if (p is IEnumerable en)
                foreach (var item in en) yield return item;
        }

        /// <summary>在参数组合上调用 Find(name)（无则递归遍历）。</summary>
        public static object? FindParameter(object? to, string name)
        {
            var p = Get(to, "Parameters");
            if (p == null) return null;
            var m = p.GetType().GetMethod("Find", new[] { typeof(string) });
            var found = m?.Invoke(p, new object[] { name });
            if (found != null) return found;
            foreach (var param in GetParameters(to))
            {
                var n = GetString(param, "Name");
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return param;
            }
            return null;
        }
    }
}
