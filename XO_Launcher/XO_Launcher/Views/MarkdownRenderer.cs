using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>把 Modrinth 项目介绍的 Markdown 渲染进 RichTextBlock。</summary>
    public static class MarkdownRenderer
    {
        private static readonly Regex Inline = new(
            @"(\*\*\*(.+?)\*\*\*|___(\*.+?)___|\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)|!\[([^\]]*)\]\([^)]+\))",
            RegexOptions.Compiled);

        public static void Render(RichTextBlock target, string markdown, int maxLines = 400)
        {
            target.Blocks.Clear();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                target.Blocks.Add(Paragraph("暂无项目介绍。", 12, false, false));
                return;
            }

            var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            if (maxLines > 0 && lines.Length > maxLines)
            {
                var kept = new string[maxLines + 1];
                Array.Copy(lines, kept, maxLines);
                kept[maxLines] = "…（介绍过长，已截断）";
                lines = kept;
            }
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new System.Text.StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    {
                        code.AppendLine(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++;
                    target.Blocks.Add(CodeParagraph(code.ToString().TrimEnd()));
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*-{3,}\s*$") || Regex.IsMatch(line, @"^\s*\*{3,}\s*$"))
                {
                    target.Blocks.Add(Paragraph("────────", 11, false, true));
                    i++;
                    continue;
                }

                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    target.Blocks.Add(Paragraph(line[2..].Trim(), 20, true, false));
                    i++;
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    target.Blocks.Add(Paragraph(line[3..].Trim(), 16, true, false));
                    i++;
                    continue;
                }
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    target.Blocks.Add(Paragraph(line[4..].Trim(), 14, true, false));
                    i++;
                    continue;
                }
                if (line.StartsWith("> ", StringComparison.Ordinal) || line.StartsWith(">", StringComparison.Ordinal))
                {
                    var quote = line.TrimStart('>', ' ');
                    target.Blocks.Add(Paragraph("“ " + quote, 12, false, true));
                    i++;
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*[-*+]\s+"))
                {
                    target.Blocks.Add(InlineParagraph("•  " + Regex.Replace(line, @"^\s*[-*+]\s+", "")));
                    i++;
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
                {
                    target.Blocks.Add(InlineParagraph(Regex.Replace(line, @"^\s*", "")));
                    i++;
                    continue;
                }

                var para = new System.Text.StringBuilder(line.TrimEnd());
                i++;
                while (i < lines.Length
                       && !string.IsNullOrWhiteSpace(lines[i])
                       && !lines[i].StartsWith('#')
                       && !lines[i].StartsWith("```", StringComparison.Ordinal)
                       && !lines[i].StartsWith("> ")
                       && !Regex.IsMatch(lines[i], @"^\s*[-*+]\s+")
                       && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s+"))
                {
                    para.Append(' ').Append(lines[i].Trim());
                    i++;
                }
                target.Blocks.Add(InlineParagraph(para.ToString()));
            }
        }

        private static Paragraph Paragraph(string text, double size, bool bold, bool secondary)
        {
            var run = new Run
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? new Windows.UI.Text.FontWeight { Weight = 600 } : new Windows.UI.Text.FontWeight { Weight = 400 },
                Foreground = new SolidColorBrush(secondary
                    ? Color.FromArgb(255, 158, 158, 158)
                    : Color.FromArgb(255, 220, 220, 220))
            };
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            p.Inlines.Add(run);
            return p;
        }

        private static Paragraph CodeParagraph(string text)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 10)
            };
            p.Inlines.Add(new Run
            {
                Text = text,
                FontFamily = ThemeService.AppFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 210, 150))
            });
            return p;
        }

        private static Paragraph InlineParagraph(string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AppendInlines(p.Inlines, text, 12);
            return p;
        }

        private static void AppendInlines(InlineCollection inlines, string text, double size)
        {
            var last = 0;
            foreach (Match match in Inline.Matches(text))
            {
                if (match.Index > last)
                    inlines.Add(NormalRun(text[last..match.Index], size));

                if (match.Groups[10].Success)
                {
                    var label = match.Groups[9].Value;
                    var href = match.Groups[10].Value;
                    if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink { NavigateUri = uri };
                        link.Foreground = new SolidColorBrush(Color.FromArgb(255, 124, 189, 75));
                        link.Inlines.Add(NormalRun(label, size));
                        inlines.Add(link);
                    }
                    else
                    {
                        inlines.Add(NormalRun(label, size));
                    }
                }
                else if (match.Groups[11].Success)
                {
                    inlines.Add(NormalRun(string.IsNullOrWhiteSpace(match.Groups[11].Value) ? "[图片]" : match.Groups[11].Value, size));
                }
                else if (match.Groups[8].Success)
                {
                    inlines.Add(new Run
                    {
                        Text = match.Groups[8].Value,
                        FontFamily = ThemeService.AppFont,
                        FontSize = size,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 210, 150))
                    });
                }
                else if (match.Groups[2].Success || match.Groups[4].Success || match.Groups[5].Success)
                {
                    var value = match.Groups[2].Success ? match.Groups[2].Value
                        : match.Groups[4].Success ? match.Groups[4].Value : match.Groups[5].Value;
                    inlines.Add(new Run
                    {
                        Text = value,
                        FontSize = size,
                        FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 230, 230, 230))
                    });
                }
                else
                {
                    var value = match.Groups[6].Success ? match.Groups[6].Value : match.Groups[7].Value;
                    inlines.Add(new Run
                    {
                        Text = value,
                        FontSize = size,
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 200, 200))
                    });
                }
                last = match.Index + match.Length;
            }
            if (last < text.Length)
                inlines.Add(NormalRun(text[last..], size));
        }

        private static Run NormalRun(string text, double size) => new()
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 184, 184, 184))
        };
    }
}
