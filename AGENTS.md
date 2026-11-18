# Cash Movements Service — coding and review rules

These are the owner's agreed conventions for this repository. Follow them for new and modified handwritten C# source and tests. Keep the assessment requirements and agreed behaviour in SOLUTION.md; follow IMPLEMENTATION_PLAN.md for scope. Do not change business rules as part of a style refactor.

## One type per file

- Put each top-level class, record, interface, enum and struct in its own file, including internal types.
- Match the filename to the type name: SubmitMovementInput.cs, RecordDisposition.cs, Submission.cs, AccountBalance.cs and IMovementStore.cs.
- Do not group types in one file merely because they are short or used together.
- Preserve namespaces and project boundaries when moving types. Do not introduce nested types just to avoid this convention.
- Leave generated code, including protobuf-generated C# and EF-generated migrations/snapshots, under its generator's conventions. The protobuf contract itself may contain multiple messages and enums.

## Use block bodies for members

- Write methods, constructors, local functions and property/indexer accessors using braces and ordinary statements. Do not use expression-bodied members, even for a single return statement.
- Put opening and closing braces on separate lines, as shown below.
- Ordinary auto-properties such as `public string Name { get; init; }` remain acceptable. Positional record declarations remain acceptable; this rule does not require replacing records with classes.

Preferred:

```csharp
public decimal GetAmount()
{
    return amount;
}

public decimal Amount
{
    get
    {
        return amount;
    }
}
```

Avoid:

```csharp
public decimal GetAmount() => amount;
public decimal Amount => amount;
```

This is not a blanket ban on the `=>` token. Lambdas used by LINQ, EF Core mappings and expression trees remain allowed, for example:

```csharp
accounts.HasKey(account => account.AccountId);
```

Do not mechanically replace expression-tree lambdas with statement-bodied lambdas; preserve their required semantics. Switch-expression arms are also distinct from expression-bodied members.

## Centralise namespace imports

- Maintain a GlobalUsings.cs file in each C# project, including test projects, for that project's explicit namespace imports.
- Use `global using` directives there instead of repeating ordinary namespace imports in individual source files, even when an import is currently used by only one file.
- Global usings apply to a project/compilation, not automatically to the entire solution. Do not add project references or move types merely to share imports.
- Do not duplicate SDK-provided implicit usings. Keep GlobalUsings.cs limited to namespaces actually needed by its project.
- Preserve necessary file-local aliases when making them global would cause a naming conflict; explain any such exception in the review handoff.
- Do not remove `using` statements/declarations that dispose resources. Those are unrelated to namespace imports.
- Do not hand-edit generated files to remove their imports.

Example GlobalUsings.cs (only include namespaces that the particular project needs):

```csharp
global using System.Globalization;
global using CashMovements.Domain;
```

## Use descriptive names

- Use clear, descriptive names for types, methods, properties, fields, parameters and local variables, including lambda parameters and test code.
- Do not shorten words merely to save typing. Prefer `cancellationToken` over `ct`, `connectionString` over `connStr`, `exception` over `ex`, `request` over `req`, and `movement` over `m`.
- Use names that explain the value's role: `decimalPointIndex`, `wholeNumberDigits` and `fractionalDigits` are clearer than `point`, `whole` and `fraction` in amount parsing.
- Use PascalCase for types, methods and public properties, and camelCase for parameters and local variables. Retain a consistent existing private-field prefix convention where present.
- Standard domain/technology terms such as Id, UTC, SQL, gRPC and DbContext need not be awkwardly expanded. Avoid unfamiliar or invented abbreviations.
- Preserve externally defined names, generated names and agreed contract fields such as protobuf `external_ref`. This naming convention does not authorise a breaking contract or database schema change.
- Use descriptive constants or enum members for values that represent named outcomes, limits or policies. Keep ordinary numeric literals inline when their meaning is clear, such as zero for an initial count.

## Use explicit variable types

- Use explicit types instead of `var` in handwritten C# source and tests, even when the type appears obvious from the initializer.
- Apply this to local variables, foreach variables, out declarations and using/await using declarations. Preserve nullable annotations where applicable.
- For example, write `TResponse response = await continuation(request, context);` and `await using CashMovementsDbContext databaseContext = await contexts.CreateDbContextAsync(cancellationToken);`.
- Prefer named types or direct projections when a local anonymous object would require `var`. Do not introduce unnecessary types solely to hold an intermediate LINQ projection; retain anonymous types inside expressions when appropriate.
- Do not hand-edit generated code to enforce this convention.

## Applying the rules

- Apply these conventions consistently throughout the current handwritten implementation when asked to perform the initial cleanup; do not fix only the screenshot examples.
- Preserve public contracts, validation behaviour, database rules and runtime behaviour during file moves and syntax changes.
- Build the affected solution and run relevant existing tests after a cleanup. Do not add tests that only check file placement or syntax preferences.
- Do not add unrelated abstractions, dependencies or features as part of the cleanup.

## Review handoff

- List every added, modified or moved handwritten source file as a clickable file link, with a short explanation of its purpose and change.
- Present files in the order the owner should read them to understand the request flow: entry point, validation, business logic and persistence.
- List tests and documentation separately. Identify old and new paths for moved types where helpful.
- State which build/tests actually ran and their results; distinguish verified behaviour from planned checks.
- Complete only the requested implementation stage. Pause for the owner's review before committing or pushing, unless the owner explicitly authorises those actions.
