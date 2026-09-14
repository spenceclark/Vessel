using System.Text.Json.Nodes;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// #83 — Anthropic server-tool blocks must reach the flattened text (MCP
/// <c>get_request include=text</c> and FTS). Fixture shapes are trimmed from request #1972.
/// </summary>
public class TextFlattenerTests
{
    private static string? Flatten(string contentJson) =>
        TextFlattener.AnthropicResponse(JsonNode.Parse($$"""{"role":"assistant","content":{{contentJson}}}"""));

    [Fact]
    public void ServerToolUse_FlattensNameAndInput()
    {
        Assert.Equal(
            """[server_tool_use web_search] {"query":"vessel proxy"}""",
            Flatten("""[{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"vessel proxy"}}]"""));
    }

    [Fact]
    public void WebSearchResults_OneLinePerResult_WithoutEncryptedContent()
    {
        string? text = Flatten(
            """
            [{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
              {"type":"web_search_result","title":"Page A","url":"https://a.example/x","page_age":"2 days ago","encrypted_content":"SECRET_A"},
              {"type":"web_search_result","title":"Page B","url":"https://b.example/y","encrypted_content":"SECRET_B"}
            ]}]
            """);

        Assert.Equal("[web_search_result] Page A — https://a.example/x\n[web_search_result] Page B — https://b.example/y", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void WebSearchError_FlattensErrorCode()
    {
        Assert.Equal(
            "[web_search_error] max_uses_exceeded",
            Flatten("""[{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}]"""));
    }

    [Fact]
    public void WebFetchResult_FlattensUrlTitleAndDocumentText()
    {
        Assert.Equal(
            "[web_fetch_result] https://docs.example/page — Docs\nFull document body.",
            Flatten(
                """
                [{"type":"web_fetch_tool_result","tool_use_id":"srvtoolu_2","content":{
                  "type":"web_fetch_result","url":"https://docs.example/page","retrieved_at":"2026-09-14T00:00:00Z",
                  "content":{"type":"document","source":{"type":"text","media_type":"text/plain","data":"Full document body."},"title":"Docs","citations":{"enabled":true}}
                }}]
                """));
    }

    [Fact]
    public void WebFetchResult_Base64Source_ContributesOnlyHeader()
    {
        Assert.Equal(
            "[web_fetch_result] https://docs.example/a.pdf — A PDF",
            Flatten(
                """
                [{"type":"web_fetch_tool_result","tool_use_id":"srvtoolu_2","content":{
                  "type":"web_fetch_result","url":"https://docs.example/a.pdf",
                  "content":{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"JVBERi0x"},"title":"A PDF"}
                }}]
                """));
    }

    [Fact]
    public void WebFetchError_FlattensErrorCode()
    {
        Assert.Equal(
            "[web_fetch_error] url_not_accessible",
            Flatten("""[{"type":"web_fetch_tool_result","tool_use_id":"srvtoolu_2","content":{"type":"web_fetch_tool_error","error_code":"url_not_accessible"}}]"""));
    }

    [Fact]
    public void UnknownToolResult_FlattensCompactContent_WithoutEncryptedFields()
    {
        string? text = Flatten(
            """[{"type":"code_execution_tool_result","tool_use_id":"srvtoolu_3","content":{"type":"code_execution_result","stdout":"hi","encrypted_stdout":"SECRET","files":[{"encrypted_blob":"SECRET"}]}}]""");

        Assert.Equal("""[code_execution_tool_result] {"type":"code_execution_result","stdout":"hi","files":[{}]}""", text);
    }

    // PR #89 review — JSON values keep non-ASCII characters rather than \uXXXX escapes, or
    // FTS can't match them.
    [Fact]
    public void CompactedJson_PreservesNonAsciiCharacters()
    {
        Assert.Equal(
            """[server_tool_use web_search] {"query":"café"}""",
            Flatten("""[{"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"café"}}]"""));
        Assert.Equal(
            """[code_execution_tool_result] {"stdout":"日本語"}""",
            Flatten("""[{"type":"code_execution_tool_result","tool_use_id":"s3","content":{"stdout":"日本語"}}]"""));
    }

    [Fact]
    public void TextWithCitations_FlattensTextOnly()
    {
        Assert.Equal(
            "Vessel is a proxy.",
            Flatten("""[{"type":"text","text":"Vessel is a proxy.","citations":[{"type":"web_search_result_location","url":"https://a.example","title":"A","cited_text":"a proxy","encrypted_index":"SECRET"}]}]"""));
    }
}
