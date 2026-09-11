#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using Splatoon.Skills;

namespace Splatoon.Tests
{
    public sealed class SkillDefinitionTests
    {
        [Test]
        public void ResolveDurationIncludesClips()
        {
            var skill = ScriptableObject.CreateInstance<SkillDefinition>();
            skill.Duration = 0.2f;
            skill.Clips.Add(new SkillClip { StartTime = 0.5f, Duration = 0.75f });
            Assert.That(skill.ResolveDuration(), Is.EqualTo(1.25f).Within(0.0001f));
            Object.DestroyImmediate(skill);
        }
    }
}
#endif
