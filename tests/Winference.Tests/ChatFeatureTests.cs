using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Winference.Core.Rag;
using Winference.Core.Runtime;
using Winference.Core.Tools;
using Winference.Core.Voice;
using Winference.Infrastructure.Chat;

namespace Winference.Tests;

public sealed class DocumentIndexTests
{
    [Fact]
    public void Small_documents_are_included_whole()
    {
        var index = new LocalVectorIndex();
        index.AddDocument("notes.txt", "The launch date is March 3. The budget is 40000 euros.");

        var prompt = index.BuildContextPrompt("summarize this", 4000);

        Assert.Contains("--- notes.txt ---", prompt);
        Assert.Contains("March 3", prompt);
        Assert.Contains("40000 euros", prompt);
    }

    [Fact]
    public void Large_documents_contribute_the_best_matching_excerpt_within_the_budget()
    {
        var index = new LocalVectorIndex();
        var filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 400));
        index.AddDocument("report.txt", filler + " The warranty period is twenty four months for every inverter. " + filler);

        var prompt = index.BuildContextPrompt("How long is the inverter warranty?", 1500);

        Assert.True(prompt.Length <= 1500);
        Assert.Contains("warranty period is twenty four months", prompt);
    }

    [Fact]
    public void Every_attached_document_is_represented_when_nothing_matches()
    {
        var index = new LocalVectorIndex();
        index.AddDocument("a.txt", string.Join(' ', Enumerable.Repeat("alpha", 2000)));
        index.AddDocument("b.txt", string.Join(' ', Enumerable.Repeat("beta", 2000)));

        var prompt = index.BuildContextPrompt("give me an overview", 3000);

        Assert.Contains("--- a.txt ---", prompt);
        Assert.Contains("--- b.txt ---", prompt);
    }

    [Fact]
    public void Removing_a_document_removes_its_text()
    {
        var index = new LocalVectorIndex();
        var document = index.AddDocument("a.txt", "secret project codename falcon");
        Assert.True(index.RemoveDocument(document.Id));
        Assert.Equal(string.Empty, index.BuildContextPrompt("falcon", 4000));
    }
}

public sealed class DocumentReaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("winference-docs-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Plain_text_and_markdown_are_read()
    {
        var path = Path.Combine(_folder, "notes.md");
        await File.WriteAllTextAsync(path, "# Title\r\nBody text");
        Assert.Equal("# Title\nBody text", await DocumentTextReader.ReadAsync(path));
    }

    [Fact]
    public async Task Html_is_reduced_to_its_text()
    {
        var path = Path.Combine(_folder, "page.html");
        await File.WriteAllTextAsync(path, "<html><style>p{}</style><script>alert(1)</script><p>Hello &amp; welcome</p></html>");
        Assert.Equal("Hello & welcome", await DocumentTextReader.ReadAsync(path));
    }

    [Fact]
    public async Task Word_documents_are_read_paragraph_by_paragraph()
    {
        var path = Path.Combine(_folder, "letter.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>Dear Sam,</w:t></w:r></w:p><w:p><w:r><w:t xml:space=\"preserve\">See you </w:t></w:r><w:r><w:t>soon.</w:t></w:r></w:p>" +
                "</w:body></w:document>"));
        }
        Assert.Equal("Dear Sam,\nSee you soon.", await DocumentTextReader.ReadAsync(path));
    }

    [Theory]
    [InlineData("scan.pdf", "PDF")]
    [InlineData("tool.exe", "isn't a supported document")]
    public async Task Unsupported_files_are_explained(string name, string expected)
    {
        var path = Path.Combine(_folder, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var error = await Assert.ThrowsAsync<RuntimeException>(() => DocumentTextReader.ReadAsync(path));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Binary_content_with_a_text_extension_is_rejected()
    {
        var path = Path.Combine(_folder, "data.txt");
        await File.WriteAllBytesAsync(path, [0x41, 0x00, 0x42, 0x00, 0x00, 0x01]);
        var error = await Assert.ThrowsAsync<RuntimeException>(() => DocumentTextReader.ReadAsync(path));
        Assert.Equal("document_binary", error.Code);
    }
}

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winference-attach-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Attachments_survive_a_new_store_and_are_scoped_to_their_chat()
    {
        var chat = Guid.NewGuid();
        var other = Guid.NewGuid();
        var added = new ConversationAttachmentStore(_root).Add(chat, @"C:\Users\me\plan.txt", "Ship the beta on Friday.");

        var reopened = new ConversationAttachmentStore(_root);
        Assert.Equal("plan.txt", Assert.Single(reopened.List(chat)).FileName);
        Assert.Empty(reopened.List(other));
        Assert.Contains("Friday", reopened.LoadIndex(chat).BuildContextPrompt("beta", 4000));

        Assert.True(reopened.Remove(chat, added.Id));
        Assert.Empty(reopened.List(chat));
    }

    [Fact]
    public void Deleting_a_chat_deletes_its_documents()
    {
        var store = new ConversationAttachmentStore(_root);
        var chat = Guid.NewGuid();
        store.Add(chat, "a.txt", "text");
        store.DeleteConversation(chat);
        Assert.Empty(store.List(chat));
        Assert.False(Directory.Exists(Path.Combine(_root, "Attachments", chat.ToString("N"))));
    }

    [Fact]
    public void A_chat_has_a_document_limit()
    {
        var store = new ConversationAttachmentStore(_root);
        var chat = Guid.NewGuid();
        for (var i = 0; i < ConversationAttachmentStore.MaxAttachmentsPerChat; i++) store.Add(chat, $"{i}.txt", "text");
        var error = Assert.Throws<RuntimeException>(() => store.Add(chat, "extra.txt", "text"));
        Assert.Equal("attachment_limit", error.Code);
    }
}

public sealed class ToolBrokerTests
{
    private static ToolExecutionBroker Broker()
    {
        var broker = new ToolExecutionBroker();
        broker.RegisterRange(BuiltInTools.GetAll());
        return broker;
    }

    [Theory]
    [InlineData("<tool_call>{\"name\": \"list_directory\", \"arguments\": {\"path\": \"C:\\\\Users\"}}</tool_call>")]
    [InlineData("Sure.\n<tool_call>\n{\"name\":\"list_directory\",\"arguments\":{\"path\":\"C:\\\\Users\"}}")]
    [InlineData("```json\n{\"name\": \"list_directory\", \"arguments\": {\"path\": \"C:\\\\Users\"}}\n```")]
    [InlineData("<tool_call>{\"name\": \"list_directory\", \"arguments\": \"{\\\"path\\\": \\\"C:\\\\\\\\Users\\\"}\"}</tool_call>")]
    [InlineData("/tool_call={\"tool_name\": \"list_directory\", \"arguments\": {\"path\": \"C:\\\\Users\"}}")]
    [InlineData("/tool_call={\"tool\": \"nope\"}\n/tool_call={\"name\": \"list_directory\", \"arguments\": {\"path\": \"C:\\\\Users\"}}")]
    [InlineData("{\"type\": \"function\", \"function\": {\"name\": \"list_directory\", \"arguments\": {\"path\": \"C:\\\\Users\"}}}")]
    [InlineData("<tool_call>\"list_directory\", {\"path\": \"C:\\\\Users\"}</tool_call>")]
    [InlineData("list_directory({\"path\": \"C:\\\\Users\"})")]
    public void Tool_requests_are_found_in_common_shapes(string reply)
    {
        var call = Broker().ParseToolCall(reply);
        Assert.NotNull(call);
        Assert.Equal("list_directory", call.Name);
        Assert.Equal(@"C:\Users", call.Arguments.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("The capital of France is Paris.")]
    [InlineData("<tool_call>{\"name\": \"delete_everything\", \"arguments\": {}}</tool_call>")]
    [InlineData("<tool_call>{not json</tool_call>")]
    [InlineData("You can ask me to run list_directory on a folder, for example {\"path\": \"C:\\\\Users\"}.")]
    [InlineData("<tool_call>\"list_directory\"</tool_call>")]
    public void Ordinary_replies_and_unknown_tools_are_not_calls(string reply) =>
        Assert.Null(Broker().ParseToolCall(reply));

    [Fact]
    public async Task Tool_results_are_bounded_and_failures_become_readable_errors()
    {
        var broker = new ToolExecutionBroker();
        broker.Register(new LocalTool("big", "Returns a lot.", [], (_, _) => Task.FromResult(new string('x', 50_000))));
        broker.Register(new LocalTool("broken", "Throws.", [], (_, _) => throw new InvalidOperationException("boom")));

        var big = await broker.ExecuteAsync(broker.ParseToolCall("<tool_call>{\"name\":\"big\"}</tool_call>")!);
        Assert.True(big.Length < ToolExecutionBroker.MaxResultChars + 32);
        var broken = await broker.ExecuteAsync(broker.ParseToolCall("<tool_call>{\"name\":\"broken\"}</tool_call>")!);
        Assert.Contains("boom", broken);
    }

    [Fact]
    public void Unreadable_requests_are_still_recognised_as_tool_requests()
    {
        const string reply = "<tool_call>please list the files</tool_call>";
        Assert.Null(Broker().ParseToolCall(reply));
        Assert.True(ToolExecutionBroker.LooksLikeToolCall(reply));
        Assert.False(ToolExecutionBroker.LooksLikeToolCall("Here are your files."));
    }

    [Fact]
    public void Tools_that_see_files_ask_first()
    {
        var broker = Broker();
        Assert.True(broker.Find("read_local_file")!.AsksFirst);
        Assert.True(broker.Find("list_directory")!.AsksFirst);
        Assert.False(broker.Find("get_current_datetime")!.AsksFirst);
    }

    [Fact]
    public void Relative_paths_are_rejected()
    {
        using var relative = JsonDocument.Parse("{\"path\": \"..\\\\secrets.txt\"}");
        Assert.Null(BuiltInTools.ResolvePath(relative.RootElement));
    }

    [Fact]
    public async Task Reading_a_file_returns_its_text()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winference-tool-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "hello from disk");
        try
        {
            var broker = Broker();
            var call = broker.ParseToolCall($"<tool_call>{JsonSerializer.Serialize(new { name = "read_local_file", arguments = new { path } })}</tool_call>")!;
            Assert.Contains("hello from disk", await broker.ExecuteAsync(call));
        }
        finally { File.Delete(path); }
    }
}

public sealed class SpeechTextTests
{
    [Fact]
    public void Markdown_formatting_is_not_read_aloud()
    {
        var spoken = SpeechText.FromMarkdown("## Steps\n\n1. Open **Settings**\n- See [the docs](https://example.com)\n\n```cs\nvar x = 1;\n```\n---\nUse `dotnet`.");
        Assert.Equal("Steps\nOpen Settings\nSee the docs\n(code block)\nUse dotnet.", spoken);
    }
}

public sealed class MediaAddonTests
{
    // Captured from Lemonade 2026.42.1 while pulling Whisper-Tiny and kokoro-v1.
    [Fact]
    public void Pull_progress_adds_up_files_and_finishes_on_complete()
    {
        var parser = new Winference.Core.Media.PullStreamParser();
        var lines = new[]
        {
            "event: progress",
            "data: {\"bytes_downloaded\":0,\"bytes_previously_downloaded\":0,\"bytes_total\":1519,\"file\":\".gitattributes\",\"file_index\":1,\"percent\":0,\"total_download_size\":3000,\"total_files\":2}",
            "",
            "event: progress",
            "data: {\"bytes_downloaded\":1000,\"bytes_previously_downloaded\":0,\"bytes_total\":1519,\"file\":\".gitattributes\",\"file_index\":1,\"percent\":66,\"total_download_size\":3000,\"total_files\":2}",
            "event: progress",
            "data: {\"bytes_downloaded\":500,\"bytes_previously_downloaded\":0,\"bytes_total\":1481,\"file\":\"model.onnx\",\"file_index\":2,\"percent\":33,\"total_download_size\":3000,\"total_files\":2}",
        };
        Winference.Core.Media.MediaDownloadProgress? last = null;
        foreach (var line in lines) last = parser.Feed(line) ?? last;
        Assert.Equal(1500, last!.BytesDone);
        Assert.Equal(0.5, last.Fraction, 3);

        Assert.Null(parser.Feed("event: complete"));
        var done = parser.Feed("data: {\"bytes_downloaded\":0,\"bytes_previously_downloaded\":0,\"bytes_total\":0,\"file\":\"\",\"file_index\":2,\"percent\":100,\"total_download_size\":0,\"total_files\":2}");
        Assert.True(done!.Complete);
        Assert.Equal(1, done.Fraction);
    }

    [Fact]
    public void Pull_errors_are_reported()
    {
        var parser = new Winference.Core.Media.PullStreamParser();
        parser.Feed("event: error");
        var error = Assert.Throws<RuntimeException>(() => parser.Feed("data: {\"error\":\"Repository not found\"}"));
        Assert.Contains("Repository not found", error.Message);
    }

    [Fact]
    public void Each_feature_has_one_default_add_on()
    {
        foreach (var kind in Enum.GetValues<Winference.Core.Media.MediaKind>())
            Assert.Single(Winference.Core.Media.MediaAddons.OfKind(kind), addon => addon.IsDefault);
    }
}

public sealed class MediaCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winference-media-").FullName;
    private string Models => Path.Combine(_root, "models");
    private string Engines => Path.Combine(_root, "bin");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Fake(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static Winference.Core.Media.MediaAddon Addon(string model) => Winference.Core.Media.MediaAddons.Find(model)!;

    [Fact]
    public void Removing_the_only_whisper_deletes_its_npu_file_folder_and_engine()
    {
        var snapshot = Path.Combine("models", "models--ggerganov--whisper.cpp", "snapshots", "abc");
        Fake(Path.Combine(snapshot, "ggml-base-encoder-vitisai.rai"), 25 * 1024 * 1024);
        Fake(Path.Combine("models", "models--ggerganov--whisper.cpp", ".lemonade_registry.json"), 100);
        Fake(Path.Combine("bin", "whispercpp", "whisper-server.exe"), 10);

        Winference.Core.Media.MediaAddons.CleanUp(Addon("Whisper-Base"), new HashSet<string>(), Models, Engines);

        Assert.False(Directory.Exists(Path.Combine(Models, "models--ggerganov--whisper.cpp")));
        Assert.False(Directory.Exists(Path.Combine(Engines, "whispercpp")));
    }

    [Fact]
    public void Another_installed_whisper_keeps_its_files_and_the_shared_engine()
    {
        var snapshot = Path.Combine("models", "models--ggerganov--whisper.cpp", "snapshots", "abc");
        var removed = Fake(Path.Combine(snapshot, "ggml-base-encoder-vitisai.rai"), 2 * 1024 * 1024);
        var kept = Fake(Path.Combine(snapshot, "ggml-small.bin"), 2 * 1024 * 1024);
        Fake(Path.Combine("bin", "whispercpp", "whisper-server.exe"), 10);

        Winference.Core.Media.MediaAddons.CleanUp(Addon("Whisper-Base"), new HashSet<string> { "Whisper-Small" }, Models, Engines);

        Assert.False(File.Exists(removed));
        Assert.True(File.Exists(kept));
        Assert.True(Directory.Exists(Path.Combine(Engines, "whispercpp")));
    }
}
