using Xunit;

// 集成测试各自创建独立 SQLite 库，但 SqlSugar 的实体映射缓存是进程级静态的；
// 并行运行测试类会出现映射串扰，因此统一串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
