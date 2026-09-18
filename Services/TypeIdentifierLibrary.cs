using System;
using System.Collections.Generic;

namespace TiaMcpServer;

/// <summary>
/// 已验证的硬件 TypeIdentifier 库。
/// 每个条目都经过实际创建测试，确认可在 TIA Portal V19 中成功创建设备。
/// 新增条目前必须实际验证：用 search_hardware_catalog 查到 → 用 create_device 创建成功。
///
/// 命名规则：
///   - 型号名：CPU 型号 + 配置（如 "CPU 1214C DC/DC/DC"）
///   - 订单号：去空格、去版本号后缀（如 "6ES7 214-1AG40-0XB0"）
///   - TypeIdentifier：完整字符串（含 OrderNumber: 前缀和 /Vx.x 版本号）
/// </summary>
public static class TypeIdentifierLibrary
{
    /// <summary>
    /// 已验证的 TypeIdentifier 条目列表。
    /// </summary>
    public class Entry
    {
        /// <summary>型号名（人类可读，如 "CPU 1214C DC/DC/DC"）</summary>
        public string ModelName { get; set; } = "";
        /// <summary>订单号（去空格、去版本号，如 "6ES7214-1AG40-0XB0"）</summary>
        public string OrderNumber { get; set; } = "";
        /// <summary>完整的 TypeIdentifier（如 "OrderNumber:6ES7 214-1AG40-0XB0/V4.6"）</summary>
        public string TypeIdentifier { get; set; } = "";
        /// <summary>描述（CPU系列、通道数等）</summary>
        public string Description { get; set; } = "";
        /// <summary>验证日期（YYYY-MM-DD）</summary>
        public string VerifiedDate { get; set; } = "";
    }

    /// <summary>
    /// 已验证的 TypeIdentifier 库。
    /// 新增条目时请按订单号排序。
    /// </summary>
    public static readonly List<Entry> VerifiedEntries = new()
    {
        // ════════════════════════════════════════════════════════════
        // SIMATIC S7-1200 CPU
        // ════════════════════════════════════════════════════════════
        new Entry
        {
            ModelName = "CPU 1214C DC/DC/DC",
            OrderNumber = "6ES7214-1AG40-0XB0",
            TypeIdentifier = "OrderNumber:6ES7 214-1AG40-0XB0/V4.6",
            Description = "S7-1200, 100KB工作内存, 14DI/10DO/2AI, DC/DC/DC",
            VerifiedDate = "2026-06-25",
        },
        new Entry
        {
            ModelName = "CPU 1214C AC/DC/Rly",
            OrderNumber = "6ES7214-1BG40-0XB0",
            TypeIdentifier = "OrderNumber:6ES7 214-1BG40-0XB0/V4.0",
            Description = "S7-1200, 100KB工作内存, 14DI/10DO/2AI, AC/DC/继电器",
            VerifiedDate = "2026-06-25",
        },
        // ════════════════════════════════════════════════════════════
        // SIMATIC HMI 触摸屏
        // 注意：6AV2124-0GC01-0AX0 和 6AV2124-0MC01-0AX0 此前被误标为
        //       "KTP400 Basic PN" 和 "KTP700 Basic PN"，
        //       经 2026-06-26 用户从博途实际确认，二者实际属于精智面板（Comfort）系列：
        //         6AV2 124-0GC01-0AX0 → TP700 Comfort（7寸精智）
        //         6AV2 124-0MC01-0AX0 → TP1200 Comfort（12寸精智）
        //       真正 KTP400 Comfort 的订单号是 6AV2 124-2DC01-0AX0。
        //       真正 KTP700 Basic（精简系列）的订单号是 6AV2 123-2GA03-0AX0。
        // ════════════════════════════════════════════════════════════
        new Entry
        {
            ModelName = "KTP400 Comfort",
            OrderNumber = "6AV2124-2DC01-0AX0",
            TypeIdentifier = "OrderNumber:6AV2 124-2DC01-0AX0/17.0.0.0",
            Description = "HMI 触摸屏, 4寸, PROFINET 接口, 精智(Comfort)系列",
            VerifiedDate = "2026-06-26",
        },
        new Entry
        {
            ModelName = "TP700 Comfort",
            OrderNumber = "6AV2124-0GC01-0AX0",
            TypeIdentifier = "OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0",
            Description = "HMI 触摸屏, 7寸, PROFINET 接口, 精智(Comfort)系列, 分辨率800x480",
            VerifiedDate = "2026-06-26",
        },
        new Entry
        {
            ModelName = "TP1200 Comfort",
            OrderNumber = "6AV2124-0MC01-0AX0",
            TypeIdentifier = "OrderNumber:6AV2 124-0MC01-0AX0/17.0.0.0",
            Description = "HMI 触摸屏, 12寸, PROFINET 接口, 精智(Comfort)系列, 分辨率1280x800",
            VerifiedDate = "2026-06-26",
        },
        new Entry
        {
            ModelName = "KTP700 Basic",
            OrderNumber = "6AV2123-2GA03-0AX0",
            TypeIdentifier = "OrderNumber:6AV2 123-2GA03-0AX0/17.0.0.0",
            // ★V17实测★ 该订单号在 V17 中创建的设备树只有 MPI/DP_CP_1 接口，
            // 无 PROFINET 接口，无法接入 PN/IE 子网。需要 PN 接口请改用
            // TP700 Comfort (6AV2124-0GC01-0AX0) 或 KTP400 Comfort (6AV2124-2DC01-0AX0)。
            Description = "HMI 触摸屏, 7寸, 精简(Basic)系列, 分辨率800x480。★实测仅 MPI/DP 接口，无 PROFINET★",
            VerifiedDate = "2026-08-12",
        },
        // ════════════════════════════════════════════════════════════
        // SIMATIC S7-1500 CPU
        // 注意：以下 TypeIdentifier 中的版本号为占位值（V0.0），
        //       需通过 search_hardware_catalog 验证实际 TypeIdentifier 后更新。
        // ════════════════════════════════════════════════════════════
        new Entry
        {
            ModelName = "CPU 1511-1 PN",
            OrderNumber = "6ES7511-1AK01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 511-1AK01-0AB0/V0.0",
            Description = "S7-1500, 入门型, 150KB工作内存, PN接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1511C-1 PN",
            OrderNumber = "6ES7511-1CK01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 511-1CK01-0AB0/V0.0",
            Description = "S7-1500C, 紧凑型, 集成DI/DO/AI/AO, PN接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1513-1 PN",
            OrderNumber = "6ES7513-1AL01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 513-1AL01-0AB0/V0.0",
            Description = "S7-1500, 标准型, 300KB工作内存, PN接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1515C-2 PN",
            OrderNumber = "6ES7515-2UM01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 515-2UM01-0AB0/V0.0",
            Description = "S7-1500C, 紧凑型, 集成DI/DO/AI/AO, 双PN接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1516-3 PN/DP",
            OrderNumber = "6ES7516-3AN01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 516-3AN01-0AB0/V0.0",
            Description = "S7-1500, 高性能, 1MB工作内存, PN/DP接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1517-3 PN/DP",
            OrderNumber = "6ES7517-3AP00-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 517-3AP00-0AB0/V0.0",
            Description = "S7-1500, 高端, 2MB工作内存, PN/DP接口",
            VerifiedDate = "待验证",
        },
        new Entry
        {
            ModelName = "CPU 1518-4 PN/DP",
            OrderNumber = "6ES7518-4AP01-0AB0",
            TypeIdentifier = "OrderNumber:6ES7 518-4AP01-0AB0/V0.0",
            Description = "S7-1500, 旗舰, 4MB工作内存, PN/DP接口",
            VerifiedDate = "待验证",
        },
    };

    /// <summary>
    /// 按订单号查找已验证的 TypeIdentifier。
    /// 订单号会自动标准化（去空格、去版本号、去 OrderNumber: 前缀）。
    /// </summary>
    /// <param name="orderNumber">订单号（如 "6ES7214-1AG40-0XB0" 或 "6ES7 214-1AG40-0XB0"）</param>
    /// <returns>匹配的 TypeIdentifier，未找到返回 null</returns>
    public static string? FindByOrderNumber(string? orderNumber)
    {
        if (string.IsNullOrEmpty(orderNumber)) return null;

        var normalized = NormalizeOrderNumber(orderNumber);

        // 1. 精确匹配标准化后的订单号
        foreach (var entry in VerifiedEntries)
        {
            if (string.Equals(entry.OrderNumber, normalized, StringComparison.OrdinalIgnoreCase))
                return entry.TypeIdentifier;
        }

        // 2. 包含匹配（处理用户输入带版本号或前缀的情况）
        foreach (var entry in VerifiedEntries)
        {
            if (entry.OrderNumber.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf(entry.OrderNumber, StringComparison.OrdinalIgnoreCase) >= 0)
                return entry.TypeIdentifier;
        }

        return null;
    }

    /// <summary>
    /// 按型号名查找已验证的 TypeIdentifier。
    /// 支持模糊匹配（如 "1214C" 匹配 "CPU 1214C DC/DC/DC"）。
    /// </summary>
    /// <param name="modelName">型号名或关键词（如 "1214C" 或 "CPU 1214C DC/DC/DC"）</param>
    /// <returns>匹配的 TypeIdentifier 列表</returns>
    public static List<Entry> FindByModelName(string? modelName)
    {
        var results = new List<Entry>();
        if (string.IsNullOrEmpty(modelName)) return results;

        var key = modelName.Trim().ToUpperInvariant();

        foreach (var entry in VerifiedEntries)
        {
            if (entry.ModelName.ToUpperInvariant().Contains(key)
                || key.Contains(entry.ModelName.ToUpperInvariant()))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    /// <summary>
    /// 标准化订单号：去空格、去 OrderNumber: 前缀、去版本号后缀。
    /// 例如 "OrderNumber:6ES7 214-1AG40-0XB0/V4.6" → "6ES7214-1AG40-0XB0"
    /// </summary>
    private static string NormalizeOrderNumber(string orderNumber)
    {
        var s = orderNumber!.Replace(" ", "").Trim();
        if (s.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("OrderNumber:".Length);
        var slashIdx = s.IndexOf('/');
        if (slashIdx > 0)
            s = s.Substring(0, slashIdx);
        return s;
    }

    /// <summary>
    /// 列出所有已验证的条目（供工具调用展示）。
    /// </summary>
    public static string ListAll()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已验证的 TypeIdentifier 库（共 {VerifiedEntries.Count} 条）：");
        sb.AppendLine();
        foreach (var entry in VerifiedEntries)
        {
            sb.AppendLine($"  型号: {entry.ModelName}");
            sb.AppendLine($"  订单号: {entry.OrderNumber}");
            sb.AppendLine($"  TypeIdentifier: {entry.TypeIdentifier}");
            sb.AppendLine($"  描述: {entry.Description}");
            sb.AppendLine($"  验证日期: {entry.VerifiedDate}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
