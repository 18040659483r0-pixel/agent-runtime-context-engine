using System.Reflection;
using System.Runtime.Serialization;
using AgentRuntime.Core.Configuration;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **A3 闸门**：配置模型 → 强类型 XML（`System.Xml.Serialization.XmlSerializer` · **元素顺序无关** · 读回跑构造函数）。
///
/// <para><b>判据</b>：从 <see cref="RuntimeConfiguration"/> 出发**递归**（本仓命名空间的配置类型 + <c>List&lt;T&gt;</c> 元素），
/// 每个公开**可写**属性要么是**配置字段**（标 <c>[DataMember]</c>），要么显式标 <c>[XmlIgnore]</c>
/// （命令行位 / 派生位 / 转发别名）。两侧都没有 → 它会**默默进配置文件**（多出不该有的键）或漏声明。</para>
/// </summary>
public sealed class RuntimeConfigurationXmlGateTests
{
    [Fact]
    public void 配置模型每个公开可写属性都必须标_DataMember_否则XML会静默丢字段()
    {
        var missing = new List<string>();
        var seen = new HashSet<Type>();

        void Walk(Type t)
        {
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    Walk(arg);
                }

                return;
            }

            if (!t.Namespace?.StartsWith("AgentRuntime", StringComparison.Ordinal) ?? true)
            {
                return;
            }

            if (t.IsEnum || t.IsPrimitive)
            {
                return;
            }

            if (!seen.Add(t))
            {
                return;
            }

            if (!Attribute.IsDefined(t, typeof(DataContractAttribute)))
            {
                missing.Add($"{t.Name}：类缺 [DataContract]");
            }

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || p.GetSetMethod() is null)
                {
                    continue;
                }

                if (Attribute.IsDefined(p, typeof(System.Xml.Serialization.XmlIgnoreAttribute)))
                {
                    continue;   // 非配置字段（命令行位 / 派生位 / 转发别名）：显式排除在 XML 之外
                }

                if (!Attribute.IsDefined(p, typeof(DataMemberAttribute)))
                {
                    missing.Add($"{t.Name}.{p.Name}：属性缺 [DataMember]（XML 会静默丢它）");
                }

                Walk(p.PropertyType);
            }
        }

        Walk(typeof(RuntimeConfiguration));

        Assert.True(missing.Count == 0,
            "A3：配置模型的 XML 序列化会**静默丢字段**（DataContractSerializer 只认 [DataMember]）：\n  - "
            + string.Join("\n  - ", missing));
    }
}
