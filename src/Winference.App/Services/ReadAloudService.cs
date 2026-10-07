using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Winference.App.Diagnostics;
using Winference.App.ViewModels;
using Winference.Core.Media;
using Winference.Core.Voice;

namespace Winference.App.Services;

/// <summary>
/// Reads replies aloud on this PC. Uses the Kokoro add-on when it is installed, otherwise the
/// voices built into Windows. Long replies are spoken in chunks so speech starts quickly.
/// </summary>
public sealed class ReadAloudService : IDisposable
{
    private const int ChunkChars = 320;
    private readonly DispatcherQueue _dispatcher;
    private readonly MediaSelection? _media;
    private SpeechSynthesizer? _synthesizer;
    private MediaPlayer? _player;
    private MediaPlaybackList? _list;
    private ChatMessageViewModel? _current;
    private CancellationTokenSource? _cancellation;
    private bool _synthesizing;
    private long _request;

    public ReadAloudService(DispatcherQueue dispatcher, MediaSelection? media = null)
    {
        _dispatcher = dispatcher;
        _media = media;
    }

    /// <summary>Starts reading the message, or stops if it is the one being read. Returns an error message on failure.</summary>
    public async Task<string?> ToggleAsync(ChatMessageViewModel message)
    {
        if (ReferenceEquals(_current, message))
        {
            Stop();
            return null;
        }
        Stop();
        var text = SpeechText.FromMarkdown(message.DisplayText);
        if (text.Length == 0) return "There's nothing to read in this message.";
        var request = ++_request;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        message.IsSpeaking = true;
        _current = message;
        try
        {
            EnsurePlayer();
            _list = new MediaPlaybackList { AutoRepeatEnabled = false };
            _player!.Source = _list;
            _synthesizing = true;
            var kokoro = _media?.ActiveModel(MediaKind.Voice);
            var started = false;
            foreach (var chunk in Chunks(text))
            {
                var stream = kokoro is not null
                    ? await KokoroAsync(kokoro, chunk, cancellation.Token)
                    : await WindowsVoiceAsync(chunk);
                if (request != _request) { stream.Dispose(); return null; }
                _list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromStream(stream, "audio/wav")));
                // Playback that ran ahead of synthesis ends early; resume it with the new chunk.
                if (!started || _player.PlaybackSession.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Paused)
                {
                    if (started) _list.MoveTo((uint)(_list.Items.Count - 1));
                    _player.Play();
                    started = true;
                }
            }
            _synthesizing = false;
            if (_player.PlaybackSession.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Paused && request == _request) Finish();
            return null;
        }
        catch (OperationCanceledException) when (request != _request) { return null; }
        catch (Exception error)
        {
            AppDiagnostics.WriteException("voice.read_aloud.failed", error);
            if (request == _request) Stop();
            return _media?.ActiveModel(MediaKind.Voice) is not null
                ? "The Kokoro voice couldn't read this. Try again, or remove Kokoro in Settings to use the Windows voice."
                : "Windows couldn't read this aloud. Check that a speech voice is installed in Settings > Time & language > Speech.";
        }
    }

    public void Stop()
    {
        ++_request;
        _synthesizing = false;
        _cancellation?.Cancel();
        _cancellation = null;
        try
        {
            _player?.Pause();
            if (_player is not null) _player.Source = null;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        _list = null;
        Finish();
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
        _player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(() => { if (!_synthesizing) Finish(); });
        _player.MediaFailed += (_, _) => _dispatcher.TryEnqueue(Stop);
    }

    private async Task<IRandomAccessStream> KokoroAsync(string model, string chunk, CancellationToken token)
    {
        var wav = await _media!.Service.SpeakAsync(model, _media.Voice, chunk, token);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(wav.AsBuffer());
        stream.Seek(0);
        return stream;
    }

    private async Task<SpeechSynthesisStream> WindowsVoiceAsync(string chunk)
    {
        _synthesizer ??= new SpeechSynthesizer();
        return await _synthesizer.SynthesizeTextToStreamAsync(chunk);
    }

    /// <summary>Sentence-aligned pieces of about <see cref="ChunkChars"/> characters.</summary>
    internal static IEnumerable<string> Chunks(string text)
    {
        var builder = new System.Text.StringBuilder();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = c is '.' or '!' or '?' or '\n' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));
            if (!boundary && i + 1 != text.Length) continue;
            builder.Append(text, start, i + 1 - start);
            start = i + 1;
            if (builder.Length >= ChunkChars || i + 1 == text.Length)
            {
                var piece = builder.ToString().Trim();
                if (piece.Length > 0) yield return piece;
                builder.Clear();
            }
        }
    }

    private void Finish()
    {
        if (_current is not null) _current.IsSpeaking = false;
        _current = null;
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
        _synthesizer?.Dispose();
        _player = null;
        _synthesizer = null;
    }
}
