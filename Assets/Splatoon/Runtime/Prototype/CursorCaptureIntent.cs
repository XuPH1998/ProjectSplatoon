namespace Splatoon.Prototype
{
    /// <summary>Gameplay intent survives temporary focus loss; native capture is gated separately.</summary>
    public sealed class CursorCaptureIntent
    {
        public bool Requested { get; private set; }
        public bool Focused { get; private set; } = true;
        public bool Paused { get; private set; }
        public bool AllowsCapture => Requested && Focused && !Paused;

        public void Request(bool capture) => Requested = capture;
        public void Focus(bool focused) => Focused = focused;
        public void Pause(bool paused) => Paused = paused;
    }

    /// <summary>Pure retry and rectangle rules; observing failures never grants capture permission.</summary>
    public sealed class CursorCaptureRearm
    {
        public const double RetrySeconds = .1;
        public const int NativeSettleFrames = 2;
        int _verifyAfterFrame;
        double _nextAttemptAt;
        public void NativeStateChanged(int frame) => _verifyAfterFrame = frame + NativeSettleFrames;
        public bool TryConsume(bool requested, bool permitted, bool nativeReadSucceeded, bool healthy, int frame, double now)
        {
            if (!requested || !permitted || !nativeReadSucceeded || healthy || frame < _verifyAfterFrame || now < _nextAttemptAt)
                return false;
            _nextAttemptAt = now + RetrySeconds;
            NativeStateChanged(frame);
            return true;
        }
        public static bool Healthy(bool nativeVisible, CursorCaptureBounds clip, CursorCaptureBounds client)
        {
            if (nativeVisible || client.Right <= client.Left || client.Bottom <= client.Top ||
                clip.Right < clip.Left || clip.Bottom < clip.Top || clip.Left < client.Left || clip.Top < client.Top ||
                clip.Right > client.Right || clip.Bottom > client.Bottom) return false;
            // This Windows Player uses a center point for Locked. Mere client
            // confinement is insufficient, especially when fullscreen equals the desktop.
            return clip.Right == clip.Left && clip.Bottom == clip.Top &&
                System.Math.Abs(2L * clip.Left - client.Left - client.Right) <= 2 &&
                System.Math.Abs(2L * clip.Top - client.Top - client.Bottom) <= 2;
        }
    }
    public readonly struct CursorCaptureBounds
    {
        public readonly int Left, Top, Right, Bottom;
        public CursorCaptureBounds(int left, int top, int right, int bottom)
        { Left = left; Top = top; Right = right; Bottom = bottom; }
    }
}
