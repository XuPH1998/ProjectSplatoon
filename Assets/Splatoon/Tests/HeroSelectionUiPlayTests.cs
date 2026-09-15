#if UNITY_EDITOR
using System.Collections;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class HeroSelectionUiPlayTests
    {
        [UnityTest] public IEnumerator PortraitPickerAtAllThreeResolutions()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            yield return RunSizes();
            yield return new ExitPlayMode();
        }
        static IEnumerator RunSizes()
        {
            foreach (var size in new[] { (1280,720), (1920,1080), (2560,1080) })
            {
                ushort port;
                using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
                HeroUiSmoke.RequestedWidth = size.Item1; HeroUiSmoke.RequestedHeight = size.Item2;
                yield return HeroUiSmoke.RunInEditorAsync(port).ToCoroutine();
            }
            HeroUiSmoke.RequestedWidth = 1280; HeroUiSmoke.RequestedHeight = 720;
        }
    }
}
#endif
