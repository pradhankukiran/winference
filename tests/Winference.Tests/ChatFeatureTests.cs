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
}
