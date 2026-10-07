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
}
