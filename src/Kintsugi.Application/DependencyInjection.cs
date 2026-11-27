using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.Application.Common.Behaviours;

namespace Kintsugi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

        services.AddScoped<HostActivity.IHostEventRecorder, HostActivity.HostEventRecorder>();
        services.AddScoped<HostActivity.HostEventPruner>();
        services.AddScoped<LogCollections.LogCollectionPruner>();
        services.AddScoped<PatchFailures.PatchFailureReconciler>();
        services.AddSingleton<PatchFailures.FailureClassificationCache>();
        services.AddScoped<PatchFailures.AiFailureClassifier>();
        services.AddSingleton<PatchFailures.Commands.DraftPatchFailureRepairs.RepairDraftAttempts>();

        return services;
    }
}
