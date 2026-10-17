#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using SimpleJSON;
using Splatoon.Config;
using Splatoon.Prototype;
using NUnit.Framework;
using UnityEngine;

namespace Splatoon.Tests
{
    // Keeps historical 32m/400-cell functional fixtures at their original
    // scale while production and current boundary tests use the shipped 8m cap.
    internal sealed class HistoricalBlockWorldHeightScope : IDisposable
    {
        readonly cfg.Tables _previousTables;
        readonly bool _previousReady;
        bool _disposed;
        static readonly PropertyInfo Tables = typeof(LubanConfigService).GetProperty("Tables");
        static readonly PropertyInfo Ready = typeof(LubanConfigService).GetProperty("IsReady");

        public static IEnumerator WaitForBootstrap()
        {
            // EnterPlayMode starts the real bootstrap. Its in-flight config
            // load must finish before a fixture swaps the tables underneath it.
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || PrototypeApp.Current.Busy)
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            var app = PrototypeApp.Current;
            Assert.That(app, Is.Not.Null, "Historical height fixture requires the completed real bootstrap.");
            Assert.That(app.Busy, Is.False); Assert.That(app.Ready, Is.True); Assert.That(app.Error, Is.Empty);
        }

        public HistoricalBlockWorldHeightScope()
        {
            var service = LubanConfigService.Current;
            _previousTables = service.Tables; _previousReady = service.IsReady;
            var data = Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban", "*.json")
                .ToDictionary(Path.GetFileNameWithoutExtension, path => JSONNode.Parse(File.ReadAllText(path)));
            data["tbglobal"][0]["blockWorldTopHeight"] = 32;
            var historical = new cfg.Tables(name => data[name]);
            Tables.SetValue(service, historical); Ready.SetValue(service, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Tables.SetValue(LubanConfigService.Current, _previousTables);
            Ready.SetValue(LubanConfigService.Current, _previousReady);
        }
    }
}
#endif
