using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Addressables;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Splatoon.Loading
{
    public sealed class AddressableSceneLoader : ISceneLoader
    {
        public async UniTask<SceneLoadHandle> LoadAsync(string address, IProgress<float> progress, CancellationToken cancellationToken)
        {
            AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(address);
            while (!handle.IsDone) { progress?.Report(handle.PercentComplete); await UniTask.Yield(cancellationToken); }
            if (handle.Status != AsyncOperationStatus.Succeeded) throw new InvalidOperationException($"Failed to load scene: {address}");
            progress?.Report(1f);
            return new SceneLoadHandle(handle.Result);
        }
        public async UniTask UnloadAsync(SceneLoadHandle handle)
        {
            await Addressables.UnloadSceneAsync(handle.Scene).Task;
        }
    }
}
