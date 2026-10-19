#if UNITY_EDITOR
using NUnit.Framework;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    // These tests never write UnityEngine.Cursor or manipulate an operating-system window.
    public sealed class PrototypeCursorCaptureTests
    {
        [Test] public void EnteringGameplayWhileUnfocusedCapturesWhenFocusArrives()
        {
            var intent = new CursorCaptureIntent();
            intent.Focus(false); intent.Request(true);
            Assert.That(intent.Requested, Is.True);
            Assert.That(intent.AllowsCapture, Is.False);
            intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.True);
        }

        [Test] public void TemporaryFocusLossReleasesWithoutForgettingGameplayRequest()
        {
            var intent = new CursorCaptureIntent(); intent.Request(true);
            Assert.That(intent.AllowsCapture, Is.True);
            intent.Focus(false);
            Assert.That(intent.AllowsCapture, Is.False);
            Assert.That(intent.Requested, Is.True);
            intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.True);
        }

        [Test] public void OpeningMenuWhileUnfocusedDoesNotRecaptureOnReturn()
        {
            var intent = new CursorCaptureIntent(); intent.Request(true);
            intent.Focus(false); intent.Request(false); intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.False);
            Assert.That(intent.Requested, Is.False);
        }

        [Test] public void PauseAndFocusEventsCanArriveInEitherOrder()
        {
            var intent = new CursorCaptureIntent(); intent.Request(true);
            intent.Pause(true); intent.Focus(false); intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.False);
            intent.Pause(false);
            Assert.That(intent.AllowsCapture, Is.True);
            intent.Focus(false); intent.Pause(true); intent.Pause(false);
            Assert.That(intent.AllowsCapture, Is.False);
            intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.True);
        }

        [Test] public void LeavingRoomOrFinishingRoundDuringPauseClearsIntent()
        {
            var intent = new CursorCaptureIntent(); intent.Request(true);
            intent.Pause(true); intent.Request(false); intent.Pause(false); intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.False);
        }

        [Test] public void ClosingMenuBeforeNativeFocusAllowsDeferredCapture()
        {
            var intent = new CursorCaptureIntent(); intent.Focus(false);
            intent.Request(false); intent.Request(true);
            Assert.That(intent.AllowsCapture, Is.False);
            intent.Focus(true);
            Assert.That(intent.AllowsCapture, Is.True);
        }

        [Test] public void NativeHealthAcceptsCenteredPointLockAndRejectsMereClientConfinement()
        {
            var client = new CursorCaptureBounds(1, 31, 1281, 751);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(641, 391, 641, 391), client), Is.True);
            Assert.That(CursorCaptureRearm.Healthy(false, client, client), Is.False);
        }
        [Test] public void FullscreenDesktopClipCannotMasqueradeAsLocked()
        {
            var fullscreen = new CursorCaptureBounds(0, 0, 2560, 1440);
            Assert.That(CursorCaptureRearm.Healthy(false, fullscreen, fullscreen), Is.False);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(1280, 720, 1280, 720), fullscreen), Is.True);
        }
        [Test] public void NativeHealthRejectsDesktopClipVisibleCursorAndWrongPoint()
        {
            var client = new CursorCaptureBounds(1, 31, 1281, 751);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(0, 0, 2560, 1440), client), Is.False);
            Assert.That(CursorCaptureRearm.Healthy(true, new CursorCaptureBounds(641, 391, 641, 391), client), Is.False);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(10, 50, 10, 50), client), Is.False);
        }
        [Test] public void NativeHealthRejectsDegenerateOrInvertedGeometry()
        {
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(0, 0, 0, 0), new CursorCaptureBounds(0, 0, 0, 0)), Is.False);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(20, 10, 10, 20), new CursorCaptureBounds(0, 0, 100, 100)), Is.False);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(50, 10, 50, 20), new CursorCaptureBounds(0, 0, 100, 100)), Is.False);
        }
        [Test] public void NativeHealthHandlesNegativeDesktopCoordinatesAndOnePixelCenterTolerance()
        {
            var client = new CursorCaptureBounds(-1280, -720, 0, 0);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(-640, -360, -640, -360), client), Is.True);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(-639, -359, -639, -359), client), Is.True);
            Assert.That(CursorCaptureRearm.Healthy(false, new CursorCaptureBounds(-638, -360, -638, -360), client), Is.False);
        }
        [Test] public void RearmRequiresRequestPermissionAndSuccessfulUnhealthyNativeObservation()
        {
            var retry = new CursorCaptureRearm();
            Assert.That(retry.TryConsume(false, true, true, false, 10, 1), Is.False);
            Assert.That(retry.TryConsume(true, false, true, false, 10, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, false, false, 10, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, true, true, 10, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 10, 1), Is.True);
        }
        [Test] public void RearmWaitsForNativeSettleAndRateLimitsRepeatedAttempts()
        {
            var retry = new CursorCaptureRearm(); retry.NativeStateChanged(100);
            Assert.That(retry.TryConsume(true, true, true, false, 100, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 101, 1.02), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 102, 1.04), Is.True);
            Assert.That(retry.TryConsume(true, true, true, false, 103, 1.08), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 104, 1.12), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 108, 1.2), Is.True);
        }
        [Test] public void FailedPermissionOrReadDoesNotConsumeRetryBudget()
        {
            var retry = new CursorCaptureRearm();
            Assert.That(retry.TryConsume(true, false, true, false, 100, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, false, false, 100, 1), Is.False);
            Assert.That(retry.TryConsume(true, true, true, false, 100, 1), Is.True);
        }
    }
}
#endif
