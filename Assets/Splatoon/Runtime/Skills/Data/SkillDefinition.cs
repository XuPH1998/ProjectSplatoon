using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Skills
{
    [Serializable] public sealed class SkillClip
    {
        [Tooltip("片段名称，仅用于编辑器识别")] public string Name = "技能片段";
        [Tooltip("片段开始时间，单位：秒"), Min(0)] public float StartTime;
        [Tooltip("片段持续时间，单位：秒"), Min(0.01f)] public float Duration = 0.5f;
        [Tooltip("触发的技能事件类型")] public SkillEventType EventType;
        [Tooltip("播放的动画片段")] public AnimationClip Animation;
        [Tooltip("生成的特效预制体")] public GameObject Vfx;
    }
    [CreateAssetMenu(fileName = "SkillDefinition", menuName = "喷墨对战/技能/技能定义")]
    public sealed class SkillDefinition : ScriptableObject
    {
        [Tooltip("技能稳定标识，代码引用使用此值，建议保留英文")] public string Id = "Skill";
        [Tooltip("技能的中文显示名称")] public string DisplayName = "技能";
        [Tooltip("最小持续时间，单位：秒；实际时长会涵盖所有片段"), Min(0.01f)] public float Duration = 1f;
        [Tooltip("按时间播放的技能片段")] public List<SkillClip> Clips = new();
        public float ResolveDuration() { float result = Duration; foreach (var c in Clips) if (c != null) result = Mathf.Max(result, c.StartTime + c.Duration); return Mathf.Max(0.01f, result); }
    }
}
