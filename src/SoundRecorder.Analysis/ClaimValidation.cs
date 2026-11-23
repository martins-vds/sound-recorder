namespace SoundRecorder.Analysis;

internal static class ClaimValidation
{
    private static readonly string[] Kinds = ["Topic", "Decision", "Action", "Report"];

    internal static void Validate(ExtractionOutput output)
    {
        ValidateShape(output);
        if (output.Limitation is not null) Text(output.Limitation, 512);
        foreach (var claim in output.Claims) Claim(claim);
    }

    private static void ValidateShape(ExtractionOutput output)
    {
        if (output.Claims is null) throw new InvalidDataException("Missing claims.");
        if (output.Claims.Length > 16) throw new InvalidDataException("Too many claims in a bounded batch.");
        if (output.Claims.Length == 0 && string.IsNullOrWhiteSpace(output.Limitation))
            throw new InvalidDataException("Empty claims require an explicit limitation.");
    }

    private static void Claim(ExtractedClaim claim)
    {
        if (claim is null) throw new InvalidDataException("Null claim.");
        if (!Kinds.Contains(claim.Kind, StringComparer.Ordinal))
            throw new InvalidDataException("Invalid transcript claim kind.");
        Text(claim.Statement, 512);
        Field(claim.Owner);
        Field(claim.Deadline);
        Qualifiers(claim.Qualifiers);
        if (claim.References is null || claim.References.Length is 0 or > 16)
            throw new InvalidDataException("Claims need bounded original source references.");
    }

    private static void Field(ClaimField field)
    {
        if (field is null) throw new InvalidDataException("Owner/deadline presence state is required.");
        switch (field.State)
        {
            case "Stated":
                Text(field.Value, 128);
                break;
            case "Absent" when field.Value is null:
                break;
            default:
                throw new InvalidDataException("Stated fields need a value; absent fields must be null.");
        }
    }

    private static void Qualifiers(string[] qualifiers)
    {
        if (qualifiers is null || qualifiers.Length > 8) throw new InvalidDataException("Bounded qualifiers are required.");
        foreach (var qualifier in qualifiers) Text(qualifier, 128);
    }

    private static void Text(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum)
            throw new InvalidDataException("Candidate field is missing or exceeds the compact record limit; no truncation.");
    }
}
