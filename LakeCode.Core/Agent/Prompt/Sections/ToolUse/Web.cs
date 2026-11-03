namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string Web = """
        Use web_search for unknown current facts and read_web for known public URLs or to
        verify a found source. Prefer primary sources and open relevant pages before drawing
        conclusions. Search returns metadata and excerpts, not proof of page claims. Operators
        and date filters are provider-dependent hints, not strict constraints; recency supports
        day/week/month/year, with explicit valid after:/before: date tokens taking precedence.
        read_web numbers extracted content lines, not metadata; request focused ranges and
        respect output and extraction caps. Each range re-extracts the page, so content can
        change between calls. read_file remains a filesystem tool, not a web reader. Do not
        use bash/curl to bypass URL rejection or provider failure. These tools do not provide
        authenticated pages, CAPTCHA handling, or interactive browser capabilities.
        """;
}
