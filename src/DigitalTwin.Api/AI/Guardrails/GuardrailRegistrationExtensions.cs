namespace DigitalTwin.Api;

public static class GuardrailRegistrationExtensions
{
    public static IServiceCollection AddDeveloperTwinGuardrails(this IServiceCollection services, TwinSettings settings)
    {
        var limits = settings.Guardrails;
        if (limits.MaxToolCalls is < 1 or > 50 || limits.MaxRepeatedToolCalls is < 1 or > 10 ||
            limits.MaxModelCalls is < 1 or > 20 || limits.RunTimeoutSeconds is < 1 or > 600 ||
            limits.MaxOutputTokens is < 128 or > 16000 || limits.MaxOutputCharacters is < 500 or > 100000)
            throw new InvalidOperationException("Twin:Guardrails execution limits are outside the supported ranges.");
        services.AddSingleton<InputGuardrailService>();
        services.AddSingleton<ToolAuthorizationService>();
        services.AddSingleton<OutputValidationService>();
        services.AddSingleton<ContentSafetyService>();
        services.AddSingleton<GuardrailAgentRunner>();
        return services;
    }
}
