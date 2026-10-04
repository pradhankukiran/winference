using Microsoft.UI.Dispatching;
using Winference.Core.Media;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Selection;

namespace Winference.App.Services;

/// <summary>Which installed Lemonade add-on each feature uses. Changes are raised on the UI thread.</summary>
public sealed class MediaSelection
{
    private readonly AppPreferencesStore _preferences;
    private readonly DispatcherQueue _dispatcher;

    public MediaSelection(LemonadeMediaService service, AppPreferencesStore preferences, DispatcherQueue dispatcher)
    {
        Service = service;
        _preferences = preferences;
        _dispatcher = dispatcher;
        service.InstalledChanged += (_, _) => RaiseChanged();
    }

    public LemonadeMediaService Service { get; }
    public event EventHandler? Changed;

    /// <summary>The preferred model if it is installed, otherwise the first installed one of that kind.</summary>
    public string? ActiveModel(MediaKind kind)
    {
        var installed = Service.Installed;
        var preferences = _preferences.Read();
        var preferred = kind switch
        {
            MediaKind.SpeechInput => preferences.SpeechInputModel,
            MediaKind.Image => preferences.ImageModel,
            MediaKind.Embeddings => preferences.EmbeddingModel,
            _ => null
        };
        if (preferred is not null && installed.Contains(preferred) && MediaAddons.Find(preferred)?.Kind == kind) return preferred;
        return MediaAddons.OfKind(kind).FirstOrDefault(addon => installed.Contains(addon.LemonadeModel))?.LemonadeModel;
    }

    public string Voice => MediaAddons.Voices.Any(voice => voice.Id == _preferences.Read().Voice) ? _preferences.Read().Voice : MediaAddons.DefaultVoice;

    public void Prefer(MediaAddon addon)
    {
        var preferences = _preferences.Read();
        _preferences.Write(addon.Kind switch
        {
            MediaKind.SpeechInput => preferences with { SpeechInputModel = addon.LemonadeModel },
            MediaKind.Image => preferences with { ImageModel = addon.LemonadeModel },
            MediaKind.Embeddings => preferences with { EmbeddingModel = addon.LemonadeModel },
            _ => preferences
        });
        RaiseChanged();
    }

    public void SetVoice(string voice)
    {
        _preferences.Write(_preferences.Read() with { Voice = voice });
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_dispatcher.HasThreadAccess) Changed?.Invoke(this, EventArgs.Empty);
        else _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
