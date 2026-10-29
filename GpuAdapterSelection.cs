namespace NEXCORE;

internal static class GpuAdapterSelection
{
    internal static T Choose<T>(IEnumerable<T> adapters, string requestedName, Func<T, string> name, Func<T, ulong> memory) where T : class
    {
        var list = adapters.ToList();
        if (string.IsNullOrWhiteSpace(requestedName))
            return list.OrderByDescending(memory).FirstOrDefault() ?? throw new InvalidOperationException("Nenhuma GPU compatível encontrada.");
        return list.FirstOrDefault(a => string.Equals(name(a).Trim(), requestedName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("A GPU selecionada não foi encontrada pelo Direct3D: " + requestedName + ". Selecione uma GPU disponível e tente novamente.");
    }
}
