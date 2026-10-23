using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 把报文记录导出为 Excel 工作簿（.xlsx）。
/// 不引入第三方 Office 组件：按 SpreadsheetML 约定直接打包一个最小 OOXML 包
/// （[Content_Types].xml + _rels + workbook + styles + 单个 worksheet），
/// 所有单元格一律写成内联字符串（t="inlineStr"），避免 Excel 把 "0x3C"、"E1 00" 之类
/// 文本自动推断成数字或日期。
/// 本类只负责按给定路径写文件：不弹对话框、不读界面状态、不改动调用方的数据。
/// </summary>
public static class CommunicationLogExcelExporter
{
    /// <summary>导出文件扩展名，供调用方配置保存对话框。</summary>
    public const string FileExtension = ".xlsx";

    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private const string WorksheetEntryName = "xl/worksheets/sheet1.xml";

    /// <summary>表头，顺序与报文输出窗口的列一一对应。</summary>
    private static readonly string[] Headers =
    [
        "时间",
        "方向",
        "类型",
        "Break",
        "Sync",
        "ID",
        "PID",
        "Data 长度",
        "Data",
        "Checksum model",
        "Checksum",
        "校验",
        "原始帧"
    ];

    /// <summary>各列宽度（字符数），与表头顺序对应，保证导出后列宽可用。</summary>
    private static readonly double[] ColumnWidths =
    [
        23, 8, 12, 10, 8, 8, 8, 12, 46, 16, 11, 30, 52
    ];

    /// <summary>导出的列数。</summary>
    public static int ColumnCount => Headers.Length;

    /// <summary>导出表头文本（只读视图，便于调用方提示或测试核对）。</summary>
    public static IReadOnlyList<string> ColumnHeaders => Headers;

    /// <summary>
    /// 把 <paramref name="entries"/> 写成一份 <paramref name="filePath"/> 指定的 Excel 工作簿。
    /// 目标目录不存在时自动创建；同名文件按覆盖写入（是否覆盖由调用方在对话框确认）。
    /// 集合为空时仍生成只有表头的合法工作簿，由调用方决定要不要拦下这种情况。
    /// </summary>
    public static void Export(string filePath, IReadOnlyList<CommunicationLogEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(entries);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteContentTypes(archive);
        WritePackageRelationships(archive);
        WriteWorkbook(archive);
        WriteWorkbookRelationships(archive);
        WriteStyles(archive);
        WriteWorksheet(archive, entries);
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "[Content_Types].xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Types", ContentTypesNamespace);

        WriteDefault(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteDefault(writer, "xml", "application/xml");

        WriteOverride(
            writer,
            "/xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteOverride(
            writer,
            "/xl/worksheets/sheet1.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        WriteOverride(
            writer,
            "/xl/styles.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteDefault(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteOverride(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WritePackageRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "_rels/.rels");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", $"{OfficeRelationshipNamespace}/officeDocument");
        writer.WriteAttributeString("Target", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/workbook.xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", "报文");
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/_rels/workbook.xml.rels");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);

        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", $"{OfficeRelationshipNamespace}/worksheet");
        writer.WriteAttributeString("Target", "worksheets/sheet1.xml");
        writer.WriteEndElement();

        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", "rId2");
        writer.WriteAttributeString("Type", $"{OfficeRelationshipNamespace}/styles");
        writer.WriteAttributeString("Target", "styles.xml");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// 最小样式表：0 号常规、1 号表头（加粗 + 浅蓝底）。
    /// Excel 要求 cellXfs 的 numFmtId/fontId/fillId/borderId 都指向存在的条目，
    /// 因此连不用的 fill/border 也要按规范补齐。
    /// </summary>
    private static void WriteStyles(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/styles.xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

        writer.WriteStartElement("fonts", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        WriteFont(writer, bold: false);
        WriteFont(writer, bold: true);
        writer.WriteEndElement();

        writer.WriteStartElement("fills", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "3");
        WriteFill(writer, null);
        WriteFill(writer, "gray125");
        WriteFill(writer, "solid", "FFDCE9F7");
        writer.WriteEndElement();

        writer.WriteStartElement("borders", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border", SpreadsheetNamespace);
        writer.WriteElementString("left", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("right", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("top", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("bottom", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("diagonal", SpreadsheetNamespace, string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        WriteCellFormat(writer, fontId: 0, fillId: 0, applyFont: false, applyFill: false);
        WriteCellFormat(writer, fontId: 1, fillId: 2, applyFont: true, applyFill: true);
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle", SpreadsheetNamespace);
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteFont(XmlWriter writer, bool bold)
    {
        writer.WriteStartElement("font", SpreadsheetNamespace);
        if (bold)
        {
            writer.WriteElementString("b", SpreadsheetNamespace, string.Empty);
        }

        writer.WriteStartElement("sz", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();
        writer.WriteStartElement("name", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "Microsoft YaHei");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFill(XmlWriter writer, string? patternType, string? solidColor = null)
    {
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace);
        if (patternType is not null)
        {
            writer.WriteAttributeString("patternType", patternType);
        }

        if (solidColor is not null)
        {
            writer.WriteStartElement("fgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("rgb", solidColor);
            writer.WriteEndElement();
            writer.WriteStartElement("bgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("indexed", "64");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteCellFormat(
        XmlWriter writer,
        int fontId,
        int fillId,
        bool applyFont,
        bool applyFill)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", "0");
        writer.WriteAttributeString("xfId", "0");
        if (applyFont)
        {
            writer.WriteAttributeString("applyFont", "1");
        }

        if (applyFill)
        {
            writer.WriteAttributeString("applyFill", "1");
        }

        writer.WriteEndElement();
    }

    private static void WriteWorksheet(ZipArchive archive, IReadOnlyList<CommunicationLogEntry> entries)
    {
        var lastRow = entries.Count + 1;
        using var writer = CreateXmlWriter(archive, WorksheetEntryName);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);

        writer.WriteStartElement("dimension", SpreadsheetNamespace);
        writer.WriteAttributeString("ref", $"A1:{ColumnName(Headers.Length)}{lastRow}");
        writer.WriteEndElement();

        // 冻结表头，滚动时列名始终可见。
        writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
        writer.WriteStartElement("sheetView", SpreadsheetNamespace);
        writer.WriteAttributeString("tabSelected", "1");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane", SpreadsheetNamespace);
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();

        writer.WriteStartElement("cols", SpreadsheetNamespace);
        for (var index = 0; index < ColumnWidths.Length; index++)
        {
            writer.WriteStartElement("col", SpreadsheetNamespace);
            writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString(
                "width",
                ColumnWidths[index].ToString("0.##", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        WriteRow(writer, rowIndex: 1, Headers, isHeader: true);
        for (var index = 0; index < entries.Count; index++)
        {
            WriteRow(writer, index + 2, BuildRowValues(entries[index]), isHeader: false);
        }

        writer.WriteEndElement();

        if (entries.Count > 0)
        {
            writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", $"A1:{ColumnName(Headers.Length)}{lastRow}");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>一行报文按界面列顺序展开为文本；时间带日期，便于脱离界面后定位。</summary>
    private static string[] BuildRowValues(CommunicationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return
        [
            entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            entry.DirectionText,
            entry.FrameTypeText,
            entry.BreakText,
            entry.SyncText,
            entry.FrameIdText,
            entry.ProtectedIdText,
            entry.DataLengthText,
            entry.PayloadText,
            entry.ChecksumTypeText,
            entry.ChecksumText,
            entry.ChecksumStatusText,
            entry.RawDataText
        ];
    }

    private static void WriteRow(
        XmlWriter writer,
        int rowIndex,
        IReadOnlyList<string> values,
        bool isHeader)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowIndex.ToString(CultureInfo.InvariantCulture));
        if (isHeader)
        {
            writer.WriteAttributeString("ht", "20");
            writer.WriteAttributeString("customHeight", "1");
        }

        for (var column = 0; column < values.Count; column++)
        {
            writer.WriteStartElement("c", SpreadsheetNamespace);
            writer.WriteAttributeString(
                "r",
                ColumnName(column + 1) + rowIndex.ToString(CultureInfo.InvariantCulture));
            if (isHeader)
            {
                writer.WriteAttributeString("s", "1");
            }

            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            // 报文里的空格有意义（如 "78 06 67 09 ..."），保留原样。
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(values[column]);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static XmlWriter CreateXmlWriter(ZipArchive archive, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        var stream = entry.Open();
        return XmlWriter.Create(
            stream,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                CloseOutput = true
            });
    }

    /// <summary>1 → A、26 → Z、27 → AA…，只用于生成单元格引用。</summary>
    private static string ColumnName(int columnIndex)
    {
        var name = string.Empty;
        var remaining = columnIndex;
        while (remaining > 0)
        {
            var offset = (remaining - 1) % 26;
            name = (char)('A' + offset) + name;
            remaining = (remaining - 1) / 26;
        }

        return name;
    }
}
