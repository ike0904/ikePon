using System;
using ikePon.Model;
using ikePon.UI.Windows;

namespace ikePon.Controller;

public sealed class MovieController
{
    private readonly AppSettings _settings;
    private MovieWindow? _window;

    public bool DisplayActive { get; private set; }
    public bool IsFullScreen => _window?.IsFullScreen ?? false;

    public event Action<bool>? DisplayActiveChanged;
    public event Action<bool>? FullScreenChanged;

    public MovieController(AppSettings settings)
    {
        _settings = settings;
        DisplayActive = settings.DisplayOutputActive;
    }

    public void OpenDisplay()
    {
        if (DisplayActive && _window != null && _window.IsVisible) return;
        DisplayActive = true;
        _settings.DisplayOutputActive = true;

        if (_window == null || !_window.IsVisible)
        {
            _window = new MovieWindow(_settings);
            _window.FullScreenChanged += OnWindowFullScreenChanged;
            _window.Closed += OnWindowClosed;
            _window.Show();

            if (_settings.MovieMode == MovieDisplayMode.FullScreen)
                _window.SetFullScreen(true);
        }

        DisplayActiveChanged?.Invoke(true);
    }

    public void CloseDisplay()
    {
        DisplayActive = false;
        _settings.DisplayOutputActive = false;

        if (_window != null)
        {
            _window.FullScreenChanged -= OnWindowFullScreenChanged;
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
        }

        DisplayActiveChanged?.Invoke(false);
        FullScreenChanged?.Invoke(false);
    }

    public void ToggleDisplay()
    {
        if (DisplayActive) CloseDisplay();
        else OpenDisplay();
    }

    public void ToggleFullScreen()
    {
        if (_window == null) return;
        bool newState = !_window.IsFullScreen;
        _window.SetFullScreen(newState);
        _settings.MovieMode = newState ? MovieDisplayMode.FullScreen : MovieDisplayMode.Window;
        FullScreenChanged?.Invoke(newState);
    }

    public void PlayVideo(string filePath, double startSec)
    {
        if (!DisplayActive || _window == null) return;
        _window.PlayVideo(filePath, startSec);
    }

    public void StopVideo()
    {
        _window?.StopVideo();
    }

    public void FadeVideo(double durationSec)
    {
        _window?.FadeVideo(durationSec);
    }

    // PANIC 1回押し：動画フェード
    public void PanicFade(double durationSec)
    {
        _window?.FadeVideo(durationSec);
    }

    // PANIC 2回押し：動画停止 + DISPLAY OFF
    public void PanicStop()
    {
        CloseDisplay();
    }

    public void ReloadStandbyImage()
    {
        _window?.LoadStandbyImage(_settings.MovieStandbyImagePath);
    }

    private void OnWindowFullScreenChanged(bool isFullScreen)
    {
        _settings.MovieMode = isFullScreen ? MovieDisplayMode.FullScreen : MovieDisplayMode.Window;
        FullScreenChanged?.Invoke(isFullScreen);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        DisplayActive = false;
        _settings.DisplayOutputActive = false;
        DisplayActiveChanged?.Invoke(false);
        FullScreenChanged?.Invoke(false);
    }
}
