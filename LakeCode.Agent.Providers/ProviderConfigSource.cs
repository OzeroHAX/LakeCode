namespace LakeCode.Agent.Providers;

public interface IProviderConfigSource
{
    T Load<T>()
        where T : class;
}

public sealed class ProviderConfigFileSource : IProviderConfigSource
{
    public T Load<T>()
        where T : class =>
        ProviderConfig.Load<T>();
}
