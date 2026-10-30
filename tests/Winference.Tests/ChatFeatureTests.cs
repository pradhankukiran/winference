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
}
