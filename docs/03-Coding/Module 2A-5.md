## Module 2A-5 — `EMS.Gateway.EdgeRuleEngine`

```
You are a senior .NET 8 engineer. Modules 2A-1 through 2A-4 are complete.

## Task
Implement `EMS.Gateway.EdgeRuleEngine` — pure transformation engine that applies CT/PT
normalization and computes virtual tags using NCalc2 expressions. This is a Class Library.
NO I/O, NO side effects, NO state between batches (except stateful functions via IEdgeStateStore).

## Project references
- EMS.Gateway.Contracts
- NuGet: NCalc2

## Classes to implement

### 1. EdgeRuleEngine : IEdgeRuleEngine

**CT/PT Normalization:**
```csharp
double physical = rawValue * template.CtRatio * template.PtRatio * register.ScaleFactor;
```

**Pre-compile NCalc expressions (at startup and on config reload):**
```csharp
// Store as ImmutableDictionary<string, NCalc.Expression>
// Compile ONCE: new Expression(expressionString)
// Evaluate MANY: expression.Evaluate() with Parameters dict

public void PrecompileExpressions(IReadOnlyList<DeviceTemplateDto> templates)
{
    foreach (var template in templates)
        foreach (var vt in template.VirtualTagExpressions)
            _compiled[key] = new Expression(vt.Expression); // compile once
}
```

**Quality Propagation (strict rules):**
```
ALL input tags = Good  → virtual tag Quality = Good
ANY input tag  = Bad   → virtual tag Quality = Bad   (Bad takes priority)
ANY input tag  = Stale, NONE = Bad → virtual tag Quality = Stale
Division by zero / NaN result → Quality = Bad, reason = "NaN_DivisionByZero"
```

**Stateful virtual tag functions (IEdgeStateStore injection):**
```csharp
// Register custom NCalc EvaluateFunction callbacks:
expression.EvaluateFunction += (name, args) =>
{
    switch (name.ToUpper())
    {
        case "TOTALIZER":
            // args[0] = tag_name, args[1] = current_value, args[2] = dt_seconds
            // result = state[key] += value * dt
            args.Result = _stateStore.Accumulate(key, value, dt);
            break;
        case "ROLLING_AVG":
            // args[0] = tag_name, args[1] = current_value, args[2] = window_seconds
            args.Result = _stateStore.RollingAverage(key, value, windowSeconds);
            break;
        case "RATE":
            // args[0] = tag_name, args[1] = current_value
            // result = (current - previous) / dt
            args.Result = _stateStore.Rate(key, value, dt);
            break;
        case "ELAPSED_ON":
            // args[0] = tag_name, args[1] = current_value, args[2] = threshold
            args.Result = _stateStore.ElapsedOn(key, value > threshold);
            break;
    }
};
```

### 2. EdgeStateStore : IEdgeStateStore (new interface, define in Contracts)
- In-memory state per virtual tag key
- Persist to SQLite every 60s (separate table from LocalBuffer — same DB file is OK)
- Load from SQLite on startup (survive restart without resetting Totalizer)
- State isolated per virtual tag — no cross-tag side effects

### 3. ValidateExpression
```csharp
public ValidationResult ValidateExpression(string expression, IReadOnlyList<string> availableTags)
{
    // Try compile: new Expression(expression)
    // Extract variable names used in expression
    // Check each variable exists in availableTags
    // Return ValidationResult with list of missing tags
}
```

## Unit tests required
1. CT/PT normalization: raw=100, ct=200, pt=1, scale=0.1 → physical=2000.0
2. Virtual tag Good: both inputs Good → result Good
3. Virtual tag Bad: one input Bad → result Bad (even if other is Good)
4. Virtual tag Stale: one input Stale, none Bad → result Stale
5. Division by zero: expression `kW/kVA` where kVA=0 → Quality=Bad, reason=NaN
6. TOTALIZER: accumulates correctly across multiple poll cycles
7. RATE: correct delta/dt calculation
8. Expression validation: references non-existent tag → ValidationResult.IsValid=false
9. Compile once: PrecompileExpressions called once; Evaluate called 10,000 times — no recompile

## Deliverable
Pure transformation library. All functions pure except IEdgeStateStore. Full unit test coverage.
```

---