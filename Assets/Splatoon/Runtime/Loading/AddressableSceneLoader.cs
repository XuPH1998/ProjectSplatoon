using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Splatoon.Loading
{
    public sealed class AddressableSceneLoader : ISceneLoader
    {
        public async UniTask<SceneLoadHandle> LoadAsync(string address, IProgress<float> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(address, UnityEngine.SceneManagement.LoadSceneMode.Additive);
            try
            {
                while (!handle.IsDone) { progress?.Report(handle.PercentComplete); await UniTask.Yield(cancellationToken); }
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.Status != AsyncOperationStatus.Succeeded) throw new InvalidOperationException($"场景加载失败：{address}", handle.OperationException);
                progress?.Report(1f);
                return new SceneLoadHandle(handle);
            }
            catch
            {
                // Addressables scene loads cannot be aborted. Finish the operation before unloading it.
                while (handle.IsValid() && !handle.IsDone) await UniTask.Yield();
                if (handle.IsValid())
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded) await Addressables.UnloadSceneAsync(handle).Task;
                    else Addressables.Release(handle);
                }
                throw;
            }
        }
        public async UniTask UnloadAsync(SceneLoadHandle handle)
        {
            if (handle.Operation.IsValid()) await Addressables.UnloadSceneAsync(handle.Operation).Task;
        }
    }
}
