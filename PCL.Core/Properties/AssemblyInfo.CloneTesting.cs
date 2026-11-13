using System.Runtime.CompilerServices;

// 测试工程需要访问 InstanceCloneEngine.BeforeFingerprint 等 internal 测试钩子。
[assembly: InternalsVisibleTo("PCL.Core.Test")]
