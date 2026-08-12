using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;

namespace RnsCompanion.Services;

/// <summary>
/// Рендер санитизированного HTML статей /api/news в нативные WPF-блоки.
/// Вход уже очищен сервером (белый список тегов), поэтому хватает простого
/// стекового парсера без внешних зависимостей. iframe/video → карточка-ссылка.
/// </summary>
internal static class NewsHtmlRenderer
{
    private static readonly HashSet<string> VoidTags = new()
    { "br", "img", "hr", "source", "input", "meta", "link" };

    private sealed class Node
    {
        public string Tag = "";
        public string Text = "";
        public string? Href, Src, Alt, Title;
        public List<Node> Children = new();
    }

    /// <summary>HTML → список блочных элементов для StackPanel.</summary>
    public static List<UIElement> Render(string html)
    {
        var blocks = new List<UIElement>();
        if (string.IsNullOrWhiteSpace(html)) return blocks;
        try
        {
            var root = Parse(html);
            RenderBlocks(root.Children, blocks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            LogService.Warn($"Новости: не удалось отрендерить HTML: {ex.Message}");
        }
        if (blocks.Count == 0)
            blocks.Add(Hint("Не удалось отобразить статью — откройте оригинал."));
        return blocks;
    }

    // ─────────────────────────── Парсер ───────────────────────────

    private static Node Parse(string html)
    {
        var root = new Node { Tag = "#root" };
        var stack = new Stack<Node>();
        stack.Push(root);

        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) { AddText(stack.Peek(), html[i..]); break; }
            if (lt > i) AddText(stack.Peek(), html[i..lt]);

            if (html.AsSpan(lt).StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var gt = html.IndexOf('>', lt);
            if (gt < 0) break;
            var tagContent = html[(lt + 1)..gt].Trim();
            i = gt + 1;
            if (tagContent.Length == 0) continue;

            if (tagContent.StartsWith('/'))
            {
                var closing = tagContent[1..].Trim().ToLowerInvariant();
                while (stack.Count > 1 && stack.Peek().Tag != closing) stack.Pop();
                if (stack.Count > 1) stack.Pop();
                continue;
            }

            var selfClose = tagContent.EndsWith('/');
            if (selfClose) tagContent = tagContent[..^1].TrimEnd();

            var sp = tagContent.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
            var name = (sp < 0 ? tagContent : tagContent[..sp]).ToLowerInvariant();
            if (name.Length == 0) continue;

            var node = new Node { Tag = name };
            if (sp >= 0) ParseAttrs(tagContent[(sp + 1)..], node);
            stack.Peek().Children.Add(node);
            if (!selfClose && !VoidTags.Contains(name))
                stack.Push(node);
        }
        return root;
    }

    private static readonly Regex AttrRe = new(
        """([\w-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')""",
        RegexOptions.Compiled);

    private static void ParseAttrs(string s, Node node)
    {
        foreach (Match m in AttrRe.Matches(s))
        {
            var value = WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "href": node.Href = value; break;
                case "src": node.Src = value; break;
                case "alt": node.Alt = value; break;
                case "title": node.Title = value; break;
            }
        }
    }

    private static void AddText(Node parent, string text)
    {
        if (text.Length == 0) return;
        if (parent.Children is [.., { Tag: "#text" } last])
            last.Text += text;
        else
            parent.Children.Add(new Node { Tag = "#text", Text = text });
    }

    // ─────────────────────────── Блоки ───────────────────────────

    private static void RenderBlocks(IEnumerable<Node> nodes, List<UIElement> output)
    {
        foreach (var node in nodes)
        {
            switch (node.Tag)
            {
                case "#text":
                    if (!string.IsNullOrWhiteSpace(node.Text))
                        output.Add(Paragraph(node));
                    break;

                case "h1" or "h2" or "h3" or "h4":
                {
                    var size = node.Tag switch { "h1" => 20.0, "h2" => 17.0, "h3" => 15.0, _ => 13.5 };
                    var tb = Paragraph(node, size, FontWeights.Bold);
                    tb.FontFamily = Res<FontFamily>("FontDisplay");
                    tb.Margin = new Thickness(0, output.Count == 0 ? 0 : 10, 0, 6);
                    output.Add(tb);
                    break;
                }

                case "p":
                {
                    // p, состоящий только из картинки — рендерим картинку на всю ширину.
                    if (SingleMeaningfulChild(node) is { Tag: "img" } img)
                    {
                        output.Add(ImageBlock(img));
                        break;
                    }
                    var tb = Paragraph(node);
                    if (tb.Inlines.Count > 0) output.Add(tb);
                    break;
                }

                case "ul" or "ol":
                    RenderList(node, output, node.Tag == "ol" ? 1 : (int?)null, 0);
                    break;

                case "blockquote":
                {
                    var inner = new List<UIElement>();
                    RenderBlocks(node.Children, inner);
                    var panel = new StackPanel();
                    foreach (var el in inner) panel.Children.Add(el);
                    output.Add(new Border
                    {
                        BorderBrush = Res<Brush>("Amber"),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(12, 2, 0, 2),
                        Margin = new Thickness(0, 4, 0, 8),
                        Child = panel,
                    });
                    break;
                }

                case "pre":
                    output.Add(new Border
                    {
                        Background = Res<Brush>("Bg1"),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 4, 0, 8),
                        Child = new TextBlock
                        {
                            Text = WebUtility.HtmlDecode(CollectText(node)).Trim(),
                            FontFamily = Res<FontFamily>("FontMono"),
                            FontSize = 11.5,
                            Foreground = Res<Brush>("Text1"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                    break;

                case "hr":
                    output.Add(new Border
                    {
                        Height = 1,
                        Background = Res<Brush>("LineStrong"),
                        Margin = new Thickness(0, 8, 0, 8),
                    });
                    break;

                case "img":
                    output.Add(ImageBlock(node));
                    break;

                case "figure":
                {
                    var inner = new List<UIElement>();
                    RenderBlocks(node.Children, inner);
                    output.AddRange(inner);
                    break;
                }

                case "figcaption":
                {
                    var tb = Paragraph(node, 11, FontWeights.Normal);
                    tb.Foreground = Res<Brush>("Text2");
                    tb.Margin = new Thickness(0, 2, 0, 8);
                    output.Add(tb);
                    break;
                }

                case "iframe" or "video":
                    output.Add(MediaCard(node));
                    break;

                case "table":
                    RenderTable(node, output);
                    break;

                // прозрачные контейнеры и всё незнакомое — рекурсия, текст не теряем
                default:
                    RenderBlocks(node.Children, output);
                    break;
            }
        }
    }

    private static void RenderList(Node list, List<UIElement> output, int? ordered, int depth)
    {
        var index = 1;
        foreach (var li in list.Children)
        {
            if (li.Tag == "#text")
            {
                if (!string.IsNullOrWhiteSpace(li.Text)) output.Add(Paragraph(li));
                continue;
            }
            if (li.Tag is "ul" or "ol") // вложенный список вне li — редкость, но не теряем
            {
                RenderList(li, output, li.Tag == "ol" ? 1 : null, depth + 1);
                continue;
            }
            if (li.Tag != "li")
            {
                RenderBlocks(new[] { li }, output);
                continue;
            }

            var marker = ordered is not null ? $"{index}." : "•";
            index++;

            var tb = Paragraph(li);
            tb.Inlines.InsertBefore(tb.Inlines.FirstInline, new Run(marker + "  ")
            {
                Foreground = Res<Brush>("Amber"),
                FontWeight = FontWeights.SemiBold,
            });
            tb.Margin = new Thickness(4 + depth * 16, 2, 0, 2);
            output.Add(tb);

            foreach (var nested in li.Children.Where(c => c.Tag is "ul" or "ol"))
                RenderList(nested, output, nested.Tag == "ol" ? 1 : null, depth + 1);
        }
        // отступ после списка
        if (output.Count > 0 && output[^1] is FrameworkElement last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 8);
    }

    private static void RenderTable(Node table, List<UIElement> output)
    {
        // Санитайзер сервера таблицы не пропускает, но на всякий случай — текстом.
        var text = CollectText(table).Trim();
        if (text.Length > 0) output.Add(Hint(text));
    }

    private static Border MediaCard(Node node)
    {
        var url = node.Src ?? "";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "видео";
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        tb.Inlines.Add(new Run("▶  ") { Foreground = Res<Brush>("Amber") });
        tb.Inlines.Add(new Run($"Видео ({host}) — смотреть в браузере")
        {
            Foreground = Res<Brush>("Text1"),
            FontWeight = FontWeights.SemiBold,
        });
        var btn = new Button
        {
            Content = tb,
            Style = (Style)Application.Current.FindResource("BtnGhost"),
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (url.Length > 0)
            btn.Click += (_, _) => OpenUrl(url);
        return new Border { Child = btn, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private static UIElement ImageBlock(Node node)
    {
        if (string.IsNullOrWhiteSpace(node.Src))
            return new Border();
        var image = new Image
        {
            Source = LoadBitmap(node.Src!),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 430,
            ToolTip = node.Alt,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Margin = new Thickness(0, 4, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = image,
        };
    }

    // ─────────────────────────── Инлайны ───────────────────────────

    private static TextBlock Paragraph(Node node, double size = 13, FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = Res<Brush>(size >= 15 ? "Text0" : "Text1"),
            FontFamily = Res<FontFamily>("FontUi"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 6),
            LineHeight = size * 1.45,
        };
        foreach (var child in node.Children)
            AddInline(tb.Inlines, child, null);
        TrimLeadingBreaks(tb.Inlines);
        return tb;
    }

    private static void AddInline(InlineCollection inlines, Node node, string? inheritedHref)
    {
        switch (node.Tag)
        {
            case "#text":
            {
                var text = WebUtility.HtmlDecode(node.Text);
                if (text.Length == 0) return;
                AddRunOrLink(inlines, text, inheritedHref);
                break;
            }

            case "br":
                inlines.Add(new LineBreak());
                break;

            case "strong" or "b":
                Wrap(inlines, node, inheritedHref, new Bold());
                break;

            case "em" or "i":
                Wrap(inlines, node, inheritedHref, new Italic());
                break;

            case "u":
                Wrap(inlines, node, inheritedHref, new Underline());
                break;

            case "s":
                Wrap(inlines, node, inheritedHref,
                    new Span { TextDecorations = TextDecorations.Strikethrough });
                break;

            case "code":
                Wrap(inlines, node, inheritedHref, new Span
                {
                    FontFamily = Res<FontFamily>("FontMono"),
                    Background = Res<Brush>("Bg1"),
                });
                break;

            case "a":
            {
                var href = node.Href ?? inheritedHref;
                foreach (var child in node.Children)
                    AddInline(inlines, child, href);
                break;
            }

            case "img":
                if (!string.IsNullOrWhiteSpace(node.Src))
                    inlines.Add(new InlineUIContainer(new Image
                    {
                        Source = LoadBitmap(node.Src!),
                        Stretch = Stretch.Uniform,
                        MaxWidth = 430,
                    }));
                break;

            case "iframe" or "video":
                inlines.Add(new LineBreak());
                inlines.Add(new InlineUIContainer(MediaCard(node)));
                inlines.Add(new LineBreak());
                break;

            // прочие инлайн-контейнеры (span и т.п.) — просто содержимое
            default:
                foreach (var child in node.Children)
                    AddInline(inlines, child, inheritedHref);
                break;
        }
    }

    private static void Wrap(InlineCollection inlines, Node node, string? href, Span wrapper)
    {
        inlines.Add(wrapper);
        foreach (var child in node.Children)
            AddInline(wrapper.Inlines, child, href);
    }

    private static void AddRunOrLink(InlineCollection inlines, string text, string? href)
    {
        if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            inlines.Add(new Run(text));
            return;
        }
        var link = new Hyperlink(new Run(text))
        {
            Foreground = Res<Brush>("Blue"),
            TextDecorations = null,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = href,
        };
        link.Click += (_, _) => OpenUrl(href);
        inlines.Add(link);
    }

    private static void TrimLeadingBreaks(InlineCollection inlines)
    {
        while (inlines.FirstInline is LineBreak)
            inlines.Remove(inlines.FirstInline);
    }

    // ─────────────────────────── Утилиты ───────────────────────────

    private static Node? SingleMeaningfulChild(Node node) =>
        node.Children.Count(c => c.Tag != "#text" || !string.IsNullOrWhiteSpace(c.Text)) == 1
            ? node.Children.First(c => c.Tag != "#text" || !string.IsNullOrWhiteSpace(c.Text))
            : null;

    private static string CollectText(Node node) =>
        node.Tag == "#text"
            ? node.Text
            : string.Concat(node.Children.Select(CollectText));

    private static BitmapImage? LoadBitmap(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = uri;
        bmp.DecodePixelWidth = 900; // не тащим многомегапиксельные обложки в память
        bmp.EndInit();
        return bmp;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("Text2"),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
    };

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogService.Warn($"Новости: не удалось открыть {url}: {ex.Message}");
        }
    }
}
