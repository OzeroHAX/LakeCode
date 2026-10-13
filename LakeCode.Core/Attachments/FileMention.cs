using System.Text;
using System.Xml;

namespace LakeCode.Core.Attachments;

/// <summary>UTF-16 range of a file chip in the displayed user text. Never inferred from typed text.</summary>
public sealed record FileMention(int Start, int Length, string Path)
{
    public static string Label(string path) => "⟦" + path + "⟧";

    public static void Validate(string text, IEnumerable<FileMention> mentions, IEnumerable<FileAttachment> files)
    {
        var paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var end = 0;
        foreach (var mention in mentions.OrderBy(item => item.Start))
        {
            if (mention.Start < end || mention.Length <= 0 || mention.Start > text.Length - mention.Length ||
                !paths.Contains(mention.Path) || text.Substring(mention.Start, mention.Length) != Label(mention.Path))
                throw new InvalidDataException("Некорректная инлайн-ссылка на файл.");
            end = mention.Start + mention.Length;
        }
    }

    public static string ForModel(string text, IEnumerable<FileMention> mentions)
    {
        var output = new StringBuilder();
        var offset = 0;
        foreach (var mention in mentions.OrderBy(item => item.Start))
        {
            output.Append(text.AsSpan(offset, mention.Start - offset));
            var reference = new StringBuilder();
            using (var writer = XmlWriter.Create(reference, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("file_ref");
                writer.WriteAttributeString("path", mention.Path);
                writer.WriteEndElement();
            }
            output.Append(reference);
            offset = mention.Start + mention.Length;
        }
        output.Append(text.AsSpan(offset));
        return output.ToString();
    }
}
