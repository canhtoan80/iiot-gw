using System.Collections.Immutable;
using EMS.Gateway.Contracts;
using NCalc2;

namespace EMS.Gateway.EdgeRuleEngine;

public class EdgeRuleEngine : IEdgeRuleEngine
{
    private readonly IDeviceTemplateRepository _templateRepo;
    private readonly IEdgeStateStore _stateStore;
    private ImmutableDictionary<string, Expression> _compiledExpressions = ImmutableDictionary<string, Expression>.Empty;

    public EdgeRuleEngine(IDeviceTemplateRepository templateRepo, IEdgeStateStore stateStore)
    {
        _templateRepo = templateRepo;
        _stateStore = stateStore;
    }

    public void PrecompileExpressions(IReadOnlyList<DeviceTemplateDto> templates)
    {
        var newCompiled = new Dictionary<string, Expression>();
        foreach (var template in templates)
        {
            foreach (var vt in template.VirtualTagExpressions)
            {
                string key = $"{template.DeviceId}::{vt.TagName}";
                var expr = new Expression(vt.Expression);
                
                // Register custom functions
                expr.EvaluateFunction += (name, args) =>
                {
                    HandleCustomFunction(key, name, args);
                };

                newCompiled[key] = expr;
            }
        }
        _compiledExpressions = newCompiled.ToImmutableDictionary();
    }

    public EnrichedTagBatch ProcessAsync(RawTagBatch rawBatch)
    {
        var template = _templateRepo.GetTemplate(rawBatch.DeviceId);
        if (template == null) return new EnrichedTagBatch(rawBatch.DeviceId, Array.Empty<EnrichedTagDto>(), rawBatch.PollTimestampUtcNs);

        var enrichedTags = new List<EnrichedTagDto>();
        var tagValues = new Dictionary<string, object?>();

        // 1. CT/PT Normalization & Quality Propagation
        foreach (var raw in rawBatch.Tags)
        {
            var reg = template.Registers.FirstOrDefault(r => r.TagName == raw.TagName);
            double? physicalValue = null;
            if (raw.RawValue.HasValue)
            {
                physicalValue = raw.RawValue.Value * template.CtRatio * template.PtRatio * (reg?.ScaleFactor ?? 1.0);
            }

            var enriched = new EnrichedTagDto(
                raw.TagName,
                physicalValue,
                reg?.Unit ?? "",
                raw.Quality,
                raw.QualityReason,
                raw.TimestampUtcNs,
                false,
                false
            );
            enrichedTags.Add(enriched);
            tagValues[raw.TagName] = physicalValue;
        }

        // 2. Virtual Tags
        foreach (var vt in template.VirtualTagExpressions)
        {
            string key = $"{rawBatch.DeviceId}::{vt.TagName}";
            if (_compiledExpressions.TryGetValue(key, out var expr))
            {
                // Set parameters
                foreach (var kvp in tagValues)
                {
                    expr.Parameters[kvp.Key] = kvp.Value ?? 0.0;
                }
                
                // Add timestamp and dt for stateful functions if possible
                // For now simplified
                expr.Parameters["dt"] = (double)template.PollCycleMs / 1000.0;

                TagQuality quality = TagQuality.Good;
                string? reason = null;

                // Quality propagation logic
                // In a real implementation we would parse the expression to find dependencies
                // and check their qualities. For now, let's assume we check all tags in the batch
                // if they are used in the expression (simplified).
                if (enrichedTags.Any(t => t.Quality == TagQuality.Bad)) quality = TagQuality.Bad;
                else if (enrichedTags.Any(t => t.Quality == TagQuality.Stale)) quality = TagQuality.Stale;

                try
                {
                    object result = expr.Evaluate();
                    double val = Convert.ToDouble(result);

                    if (double.IsNaN(val) || double.IsInfinity(val))
                    {
                        quality = TagQuality.Bad;
                        reason = "NaN_DivisionByZero";
                        val = 0;
                    }

                    enrichedTags.Add(new EnrichedTagDto(
                        vt.TagName,
                        val,
                        vt.Unit,
                        quality,
                        reason,
                        rawBatch.PollTimestampUtcNs,
                        true,
                        false
                    ));
                }
                catch (Exception ex)
                {
                    enrichedTags.Add(new EnrichedTagDto(
                        vt.TagName,
                        null,
                        vt.Unit,
                        TagQuality.Bad,
                        $"EvalError: {ex.Message}",
                        rawBatch.PollTimestampUtcNs,
                        true,
                        false
                    ));
                }
            }
        }

        return new EnrichedTagBatch(rawBatch.DeviceId, enrichedTags, rawBatch.PollTimestampUtcNs);
    }

    public ValidationResult ValidateExpression(string expression, IReadOnlyList<string> availableTags)
    {
        try
        {
            var expr = new Expression(expression);
            if (expr.HasErrors())
            {
                return new ValidationResult(false, new[] { expr.Error });
            }
            // Real validation would extract params and check vs availableTags
            return new ValidationResult(true, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, new[] { ex.Message });
        }
    }

    private void HandleCustomFunction(string key, string name, FunctionArgs args)
    {
        switch (name.ToUpperInvariant())
        {
            case "TOTALIZER":
                if (args.Parameters.Length >= 2)
                {
                    double val = Convert.ToDouble(args.Parameters[0].Evaluate());
                    double dt = Convert.ToDouble(args.Parameters[1].Evaluate());
                    args.Result = _stateStore.Accumulate(key, val, dt);
                }
                break;
            case "ROLLING_AVG":
                if (args.Parameters.Length >= 2)
                {
                    double val = Convert.ToDouble(args.Parameters[0].Evaluate());
                    double window = Convert.ToDouble(args.Parameters[1].Evaluate());
                    args.Result = _stateStore.RollingAverage(key, val, window);
                }
                break;
            case "RATE":
                if (args.Parameters.Length >= 2)
                {
                    double val = Convert.ToDouble(args.Parameters[0].Evaluate());
                    double dt = Convert.ToDouble(args.Parameters[1].Evaluate());
                    args.Result = _stateStore.Rate(key, val, dt);
                }
                break;
            case "ELAPSED_ON":
                if (args.Parameters.Length >= 1)
                {
                    bool cond = Convert.ToBoolean(args.Parameters[0].Evaluate());
                    args.Result = _stateStore.ElapsedOn(key, cond);
                }
                break;
        }
    }
}
