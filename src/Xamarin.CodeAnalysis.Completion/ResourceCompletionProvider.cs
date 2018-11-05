﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace Xamarin.CodeAnalysis
{
    [ExportCompletionProvider(nameof(ResourceCompletionProvider), LanguageNames.CSharp)]
    public class ResourceCompletionProvider : CompletionProvider
    {
        private static readonly CompletionItemRules StandardCompletionRules = CompletionItemRules.Default.WithSelectionBehavior(CompletionItemSelectionBehavior.SoftSelection);

        public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
        {
            // TODO: should trigger if we're inside a string
            return base.ShouldTriggerCompletion(text, caretPosition, trigger, options);
        }

        public override Task<CompletionDescription> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
        {
            // TODO: get the actual xml file location.
            return Task.FromResult(CompletionDescription.FromText($"{item.Properties["Path"]}({item.Properties["Line"]},{item.Properties["Position"]})"));
        }

        public override Task<CompletionChange> GetChangeAsync(Document document, CompletionItem item, char? commitKey, CancellationToken cancellationToken)
        {
            // Determine change to insert
            return base.GetChangeAsync(document, item, commitKey, cancellationToken);
        }

        static readonly Regex isResourceValue = new Regex(@"Resources\\values\\.*.xml", RegexOptions.Compiled);

        public override async Task ProvideCompletionsAsync(CompletionContext completionContext)
        {
            if (!completionContext.Document.SupportsSemanticModel)
                return;

            var position = completionContext.Position;
            var document = completionContext.Document;
            var cancellationToken = completionContext.CancellationToken;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var span = completionContext.CompletionListSpan;
            var token = root.FindToken(span.Start);
            var node = token.Parent?.AncestorsAndSelf().FirstOrDefault(a => a.FullSpan.Contains(span));
            if (node is LiteralExpressionSyntax literal &&
                node?.Parent is AttributeArgumentSyntax argument && 
                node?.Parent?.Parent?.Parent is AttributeSyntax attribute)
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                var symbol = semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol;
                if (symbol?.ContainingType.ToDisplayString() == "Android.App.ActivityAttribute" || 
                    (symbol == null && attribute.Name.ToString() == "Activity"))
                {
                    var name = argument.NameEquals.Name.ToString();
                    // TODO: consider resource files some other way?
                    var valueDocs = document.Project.AdditionalDocuments.Where(doc => isResourceValue.IsMatch(doc.FilePath));
                    var elementName = "string";
                    if (name == "Theme")
                        elementName = "style";

                    var strings = new HashSet<string>();
                    var styles = new HashSet<string>();
                    var xmlSettings = new XmlReaderSettings
                    {
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true,
                        IgnoreWhitespace = true,
                    };

                    foreach (var doc in valueDocs)
                    {
                        XmlReader reader = null;
                        try
                        {
                            if (File.Exists(doc.FilePath))
                            {
                                reader = XmlReader.Create(doc.FilePath, xmlSettings);
                            }
                            else
                            {
                                // In tests, the file doesn't exist.
                                var writer = new StringWriter();
                                (await doc.GetTextAsync(cancellationToken)).Write(writer, cancellationToken);
                                reader = XmlReader.Create(new StringReader(writer.ToString()), xmlSettings);
                            }

                            if (reader.MoveToContent() == XmlNodeType.Element)
                            {
                                // TODO: cache already parsed results as long as the text version remains the same?
                                while (reader.Read())
                                {
                                    if (reader.LocalName == elementName && reader.GetAttribute("name") is string id)
                                    {
                                        completionContext.AddItem(CompletionItem.Create($"@{reader.LocalName}/{id}",
                                            tags: ImmutableArray.Create(WellKnownTags.Constant, "Xamarin"),
                                            properties: ImmutableDictionary.Create<string, string>()
                                                .Add("Path", doc.FilePath)
                                                .Add("Line", ((IXmlLineInfo)reader).LineNumber.ToString())
                                                .Add("Position", ((IXmlLineInfo)reader).LinePosition.ToString()),
                                            rules: StandardCompletionRules));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            reader?.Dispose();
                        }
                    }
                }
            }

            await Task.CompletedTask;
        }
    }
}
