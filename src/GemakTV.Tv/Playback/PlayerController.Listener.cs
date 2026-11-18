using AndroidX.Media3.Common;
using AndroidX.Media3.Common.Text;

namespace GemakTV.Tv.Playback;

/// <summary>
/// Every other Player.Listener callback, deliberately empty.
/// <para>
/// They must exist: below Android 7 (API 24) Java default interface methods are compiled away ("desugared"), so a
/// C# class that leaves one to its default implementation crashes with AbstractMethodError when ExoPlayer calls it.
/// </para>
/// </summary>
public sealed partial class PlayerController
{
    public void OnAudioAttributesChanged(AudioAttributes? audioAttributes) { }
    public void OnAudioSessionIdChanged(int audioSessionId) { }
    public void OnAvailableCommandsChanged(PlayerCommands? availableCommands) { }
    public void OnCues(CueGroup? cueGroup) { }
    public void OnCuesDeprecated(IList<Cue>? cues) { }
    public void OnDeviceInfoChanged(DeviceInfo? deviceInfo) { }
    public void OnDeviceVolumeChanged(int volume, bool muted) { }
    public void OnEvents(IPlayer? player, PlayerEvents? events) { }
    public void OnIsLoadingChanged(bool isLoading) { }
    public void OnLoadingChanged(bool isLoading) { }
    public void OnMaxSeekToPreviousPositionChanged(long maxSeekToPreviousPositionMs) { }
    public void OnMediaItemTransition(MediaItem? mediaItem, int reason) { }
    public void OnMediaMetadataChanged(MediaMetadata? mediaMetadata) { }
    public void OnMetadata(Metadata? metadata) { }
    public void OnPlayWhenReadyChanged(bool playWhenReady, int reason) { }
    public void OnPlaybackParametersChanged(PlaybackParameters? playbackParameters) { }
    public void OnPlaybackSuppressionReasonChanged(int playbackSuppressionReason) { }
    public void OnPlayerErrorChanged(PlaybackException? error) { }
    public void OnPlayerStateChanged(bool playWhenReady, int playbackState) { }
    public void OnPlaylistMetadataChanged(MediaMetadata? mediaMetadata) { }
    public void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason) { }
    public void OnRenderedFirstFrame() { }
    public void OnRepeatModeChanged(int repeatMode) { }
    public void OnSeekBackIncrementChanged(long seekBackIncrementMs) { }
    public void OnSeekForwardIncrementChanged(long seekForwardIncrementMs) { }
    public void OnShuffleModeEnabledChanged(bool shuffleModeEnabled) { }
    public void OnSkipSilenceEnabledChanged(bool skipSilenceEnabled) { }
    public void OnSurfaceSizeChanged(int width, int height) { }
    public void OnTimelineChanged(Timeline? timeline, int reason) { }
    public void OnTrackSelectionParametersChanged(TrackSelectionParameters? parameters) { }
    public void OnTracksChanged(Tracks? tracks) { }
    public void OnVideoSizeChanged(VideoSize? videoSize) { }
    public void OnVolumeChanged(float volume) { }
}
