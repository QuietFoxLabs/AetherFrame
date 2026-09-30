using System;
using AngleSharp.Html.Parser;

namespace AetherFrame.Server.Lodestone;

/// <summary>What a Lodestone character page shows that a check needs (decision C2), and nothing more.</summary>
internal sealed record LodestoneCharacter(string Name, string World, string SelfIntroduction);

/// <summary>
/// Reads a Lodestone character page (decision C2). Each field comes from its one element: the name
/// from <c>p.frame__chara__name</c>, the Home World from <c>p.frame__chara__world</c> (shown as
/// "World [Data Center]"), and the profile text from <c>div.character__selfintroduction</c>. Zero or
/// several of any, a name the game wouldn't allow, or a World not on the list, and the page is
/// refused. Nothing else on the page is read, and the page is never kept.
/// </summary>
internal static class LodestonePage
{
    private static readonly HtmlParser Parser = new(new HtmlParserOptions { IsScripting = false });

    public static LodestoneCharacter? Read(string html, Worlds worlds)
    {
        var document = Parser.ParseDocument(html);
        var names = document.QuerySelectorAll("p.frame__chara__name");
        var homeWorlds = document.QuerySelectorAll("p.frame__chara__world");
        var introductions = document.QuerySelectorAll("div.character__selfintroduction");
        if (names.Length != 1 || homeWorlds.Length != 1 || introductions.Length != 1)
        {
            return null;
        }

        var name = names[0].TextContent.Trim();
        if (!CharacterNames.IsGameName(name))
        {
            return null;
        }

        var shown = homeWorlds[0].TextContent.Trim();
        var bracket = shown.IndexOf(" [", StringComparison.Ordinal);
        var worldText = bracket < 0 ? shown : shown[..bracket];
        if (!worlds.TryFind(worldText, out var world))
        {
            return null;
        }

        return new LodestoneCharacter(name, world, introductions[0].TextContent);
    }

    /// <summary>
    /// Whether a 404's body is the Lodestone's own "not found" page, rather than an error from
    /// anything between: only that page counts toward removing a binding (decision C1).
    /// </summary>
    public static bool IsNotFoundPage(string html)
    {
        var document = Parser.ParseDocument(html);
        return document.QuerySelectorAll(".error__body").Length == 1
            && document.QuerySelectorAll("p.frame__chara__name").Length == 0
            && string.Equals(document.Title?.Trim(), "FINAL FANTASY XIV, The Lodestone", StringComparison.Ordinal);
    }
}
