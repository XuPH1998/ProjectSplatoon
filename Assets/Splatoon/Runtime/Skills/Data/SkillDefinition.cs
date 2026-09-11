using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Skills
{
    [Serializable] public sealed class SkillClip { public string Name = "Clip"; [Min(0)] public float StartTime; [Min(0.01f)] public float Duration = 0.5f; public SkillEventType EventType; public AnimationClip Animation; public GameObject Vfx; }
    [CreateAssetMenu(fileName = "SkillDefinition", menuName = "Project Splatoon/Skills/Skill Definition")]
    public sealed class SkillDefinition : ScriptableObject
    {
        public string Id = "Skill"; public string DisplayName = "Skill"; [Min(0.01f)] public float Duration = 1f; public List<SkillClip> Clips = new();
        public float ResolveDuration() { float result = Duration; foreach (var c in Clips) if (c != null) result = Mathf.Max(result, c.StartTime + c.Duration); return Mathf.Max(0.01f, result); }
    }
}
