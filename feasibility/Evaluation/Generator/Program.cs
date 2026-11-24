using System.Globalization;
using System.Text;
using System.Text.Json;
using Evaluation.Generator;

Console.OutputEncoding = new UTF8Encoding(false);
var seed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[0]))!;
CorpusGenerator.Write(Console.Out, seed, int.Parse(args[1], CultureInfo.InvariantCulture), bool.Parse(args[2]));
