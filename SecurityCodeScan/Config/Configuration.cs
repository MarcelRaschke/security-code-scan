﻿#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Analyzer.Utilities;
using Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis;
using Analyzer.Utilities.PooledObjects;
using Microsoft.CodeAnalysis;
using SecurityCodeScan.Analyzers.Taint;
using SecurityCodeScan.Analyzers.Utils;

namespace SecurityCodeScan.Config
{
    internal class TaintConfiguration
    {
        private WellKnownTypeProvider WellKnownTypeProvider { get; set; }

        private ImmutableDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SourceInfo>>> SourceSymbolMap { get; set; }

        private ImmutableDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SanitizerInfo>>> SanitizerSymbolMap { get; set; }

        private ImmutableDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SinkInfo>>> SinkSymbolMap { get; set; }

        public TaintedDataSymbolMap<SourceInfo> GetSourceSymbolMap(SinkKind sinkKind)
        {
            return this.GetFromMap<SourceInfo>(sinkKind, this.SourceSymbolMap);
        }

        public TaintedDataSymbolMap<SanitizerInfo> GetSanitizerSymbolMap(SinkKind sinkKind)
        {
            return this.GetFromMap<SanitizerInfo>(sinkKind, this.SanitizerSymbolMap);
        }

        public TaintedDataSymbolMap<SinkInfo> GetSinkSymbolMap(SinkKind sinkKind)
        {
            return this.GetFromMap<SinkInfo>(sinkKind, this.SinkSymbolMap);
        }

        public bool HasTaintArraySource(SinkKind sinkKind, Configuration config)
        {
            return GetSourceInfos(sinkKind, config).Any(o => o.TaintConstantArray);
        }

        private TaintedDataSymbolMap<T> GetFromMap<T>(SinkKind sinkKind, ImmutableDictionary<SinkKind, Lazy<TaintedDataSymbolMap<T>>> map)
            where T : ITaintedDataInfo
        {
            if (map.TryGetValue(sinkKind, out Lazy<TaintedDataSymbolMap<T>> lazySourceSymbolMap))
            {
                return lazySourceSymbolMap.Value;
            }
            else
            {
                Debug.Fail($"SinkKind {sinkKind} entry missing from {typeof(T).Name} map");
                return new TaintedDataSymbolMap<T>(this.WellKnownTypeProvider, Enumerable.Empty<T>());
            }
        }

        public TaintConfiguration(Compilation compilation, Configuration config)
        {
            WellKnownTypeProvider = WellKnownTypeProvider.GetOrCreate(compilation);
            using PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SourceInfo>>> sourceSymbolMapBuilder =
                PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SourceInfo>>>.GetInstance();
            using PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SanitizerInfo>>> sanitizerSymbolMapBuilder =
                PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SanitizerInfo>>>.GetInstance();
            using PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SinkInfo>>> sinkSymbolMapBuilder =
                PooledDictionary<SinkKind, Lazy<TaintedDataSymbolMap<SinkInfo>>>.GetInstance();

            // For tainted data rules with the same set of sources, we'll reuse the same TaintedDataSymbolMap<SourceInfo> instance.
            // Same for sanitizers.
            using PooledDictionary<ImmutableHashSet<SourceInfo>, Lazy<TaintedDataSymbolMap<SourceInfo>>> sourcesToSymbolMap =
                PooledDictionary<ImmutableHashSet<SourceInfo>, Lazy<TaintedDataSymbolMap<SourceInfo>>>.GetInstance();
            using PooledDictionary<ImmutableHashSet<SanitizerInfo>, Lazy<TaintedDataSymbolMap<SanitizerInfo>>> sanitizersToSymbolMap =
                PooledDictionary<ImmutableHashSet<SanitizerInfo>, Lazy<TaintedDataSymbolMap<SanitizerInfo>>>.GetInstance();

            // Build a mapping of (sourceSet, sanitizerSet) -> (sinkKinds, sinkSet), so we'll reuse the same TaintedDataSymbolMap<SinkInfo> instance.
            using PooledDictionary<(ImmutableHashSet<SourceInfo> SourceInfos, ImmutableHashSet<SanitizerInfo> SanitizerInfos), (ImmutableHashSet<SinkKind>.Builder SinkKinds, ImmutableHashSet<SinkInfo>.Builder SinkInfos)> sourceSanitizersToSinks =
                PooledDictionary<(ImmutableHashSet<SourceInfo> SourceInfos, ImmutableHashSet<SanitizerInfo> SanitizerInfos), (ImmutableHashSet<SinkKind>.Builder SinkKinds, ImmutableHashSet<SinkInfo>.Builder SinkInfos)>.GetInstance();

            // Using LazyThreadSafetyMode.ExecutionAndPublication to avoid instantiating multiple times.
            foreach (SinkKind sinkKind in Enum.GetValues(typeof(TaintType)))
            {
                ImmutableHashSet<SourceInfo> sources = GetSourceInfos(sinkKind, config);
                if (!sourcesToSymbolMap.TryGetValue(sources, out Lazy<TaintedDataSymbolMap<SourceInfo>> lazySourceSymbolMap))
                {
                    lazySourceSymbolMap = new Lazy<TaintedDataSymbolMap<SourceInfo>>(
                        () => { return new TaintedDataSymbolMap<SourceInfo>(WellKnownTypeProvider, sources); },
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    sourcesToSymbolMap.Add(sources, lazySourceSymbolMap);
                }

                sourceSymbolMapBuilder.Add(sinkKind, lazySourceSymbolMap);

                ImmutableHashSet<SanitizerInfo> sanitizers = GetSanitizerInfos(sinkKind, config);
                if (!sanitizersToSymbolMap.TryGetValue(sanitizers, out Lazy<TaintedDataSymbolMap<SanitizerInfo>> lazySanitizerSymbolMap))
                {
                    lazySanitizerSymbolMap = new Lazy<TaintedDataSymbolMap<SanitizerInfo>>(
                        () => { return new TaintedDataSymbolMap<SanitizerInfo>(WellKnownTypeProvider, sanitizers); },
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    sanitizersToSymbolMap.Add(sanitizers, lazySanitizerSymbolMap);
                }

                sanitizerSymbolMapBuilder.Add(sinkKind, lazySanitizerSymbolMap);

                ImmutableHashSet<SinkInfo> sinks = GetSinkInfos(sinkKind, config);
                if (!sourceSanitizersToSinks.TryGetValue((sources, sanitizers), out (ImmutableHashSet<SinkKind>.Builder SinkKinds, ImmutableHashSet<SinkInfo>.Builder SinkInfos) sinksPair))
                {
                    sinksPair = (ImmutableHashSet.CreateBuilder<SinkKind>(), ImmutableHashSet.CreateBuilder<SinkInfo>());
                    sourceSanitizersToSinks.Add((sources, sanitizers), sinksPair);
                }

                sinksPair.SinkKinds.Add(sinkKind);
                sinksPair.SinkInfos.UnionWith(sinks);
            }

            foreach (KeyValuePair<(ImmutableHashSet<SourceInfo> SourceInfos, ImmutableHashSet<SanitizerInfo> SanitizerInfos), (ImmutableHashSet<SinkKind>.Builder SinkKinds, ImmutableHashSet<SinkInfo>.Builder SinkInfos)> kvp in sourceSanitizersToSinks)
            {
                ImmutableHashSet<SinkInfo> sinks = kvp.Value.SinkInfos.ToImmutable();
                Lazy<TaintedDataSymbolMap<SinkInfo>> lazySinkSymbolMap = new Lazy<TaintedDataSymbolMap<SinkInfo>>(
                    () => { return new TaintedDataSymbolMap<SinkInfo>(WellKnownTypeProvider, sinks); },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                foreach (SinkKind sinkKind in kvp.Value.SinkKinds)
                {
                    sinkSymbolMapBuilder.Add(sinkKind, lazySinkSymbolMap);
                }
            }

            SourceSymbolMap = sourceSymbolMapBuilder.ToImmutableDictionary();
            SanitizerSymbolMap = sanitizerSymbolMapBuilder.ToImmutableDictionary();
            SinkSymbolMap = sinkSymbolMapBuilder.ToImmutableDictionary();
        }

        private class AggregatedSource
        {
            public TaintEntryPointData entryPoint;
            public TaintSource source;
            public Sanitizer sanitizer;
            public Transfer transfer;
        }

        private ImmutableHashSet<SourceInfo> GetSourceInfos(SinkKind sinkKind, Configuration config)
        {
            var typeToInfos = new Dictionary<string, AggregatedSource>();

            foreach (var entryPoint in config.TaintEntryPoints)
            {
                if (!typeToInfos.TryGetValue(entryPoint.Key, out var value))
                {
                    value = new AggregatedSource();
                    typeToInfos.Add(entryPoint.Key, value);
                }

                if (value.entryPoint != null)
                    throw new ArgumentException($"Duplicate entrypoint for type '{entryPoint.Key}'");

                value.entryPoint = entryPoint.Value;
            }

            foreach (var source in config.TaintSources)
            {
                if (!typeToInfos.TryGetValue(source.Type, out var value))
                {
                    value = new AggregatedSource();
                    typeToInfos.Add(source.Type, value);
                }

                if (value.source != null)
                    throw new ArgumentException($"Duplicate taint source for type '{source.Type}'");

                value.source = source;
            }

            foreach (var sanitizer in config.Sanitizers)
            {
                var methods = sanitizer.Methods.Where(x => x.InOut != null && x.InOut.Any(io => io.outArgumentName != TaintedTargetValue.Return));
                if (!methods.Any())
                    continue;

                if (!typeToInfos.TryGetValue(sanitizer.Type, out var value))
                {
                    value = new AggregatedSource();
                    typeToInfos.Add(sanitizer.Type, value);
                }

                if (value.sanitizer != null)
                    throw new ArgumentException($"Duplicate sanitizer for type '{sanitizer.Type}'");

                value.sanitizer = sanitizer;
            }

            foreach (var transfer in config.Transfers)
            {
                if (!typeToInfos.TryGetValue(transfer.Type, out var value))
                {
                    value = new AggregatedSource();
                    typeToInfos.Add(transfer.Type, value);
                }

                if (value.transfer != null)
                    throw new ArgumentException($"Duplicate taint source for type '{transfer.Type}'");

                value.transfer = transfer;
            }

            var sourceInfosBuilder = PooledHashSet<SourceInfo>.GetInstance();

            foreach(var type in typeToInfos)
            {
                bool? isIterface = null;

                if (type.Value.sanitizer?.IsInterface != null )
                    if (isIterface.HasValue && isIterface != type.Value.sanitizer.IsInterface)
                        throw new ArgumentException($"Inconsistent 'IsInterface' for type '{type.Key}'");
                    else
                        isIterface = type.Value.sanitizer.IsInterface;

                if (type.Value.source?.IsInterface != null)
                    if (isIterface.HasValue && isIterface != type.Value.sanitizer.IsInterface)
                        throw new ArgumentException($"Inconsistent 'IsInterface' for type '{type.Key}'");
                    else
                        isIterface = type.Value.source.IsInterface;

                if (type.Value.transfer?.IsInterface != null)
                    if (isIterface.HasValue && isIterface != type.Value.transfer.IsInterface)
                        throw new ArgumentException($"Inconsistent 'IsInterface' for type '{type.Key}'");
                    else
                        isIterface = type.Value.sanitizer.IsInterface;

                SourceInfo metadata = new SourceInfo(
                    type.Key,
                    isInterface: isIterface ?? false,
                    taintedProperties: type.Value.source?.Properties?.ToImmutableHashSet(StringComparer.Ordinal)
                        ?? ImmutableHashSet<string>.Empty,
                    taintedArguments: type.Value.entryPoint != null ?
                        new ParameterMatcher[]{
                            (parameter) => {
                                ISymbol containingSymbol = parameter.ContainingSymbol;

                                if (type.Value.entryPoint.Class != null)
                                {
                                    ITypeSymbol classSymbol = containingSymbol.ContainingSymbol as ITypeSymbol;
                                    if (classSymbol != null)
                                    {
                                        if (classSymbol.HasDerivedClassAttribute(x => type.Value.entryPoint.Class.ExcludeAttributes.Contains(x.AttributeClass.ToString())))
                                            return false;
                                    }
                                }

                                if (type.Value.entryPoint.Method != null)
                                {
                                    if (type.Value.entryPoint.Method.Static.HasValue && type.Value.entryPoint.Method.Static != containingSymbol.IsStatic)
                                        return false;

                                    if (type.Value.entryPoint.Method.Name != null)
                                        return type.Value.entryPoint.Method.Name == containingSymbol.Name;

                                    if (type.Value.entryPoint.Method.IncludeConstructor.HasValue && type.Value.entryPoint.Method.IncludeConstructor != containingSymbol.IsConstructor())
                                        return false;

                                    if (type.Value.entryPoint.Method.Accessibility.All(a => a != containingSymbol.DeclaredAccessibility))
                                        return false;

                                    IMethodSymbol methodSymbol = containingSymbol as IMethodSymbol;
                                    if (methodSymbol != null)
                                    {
                                        if (methodSymbol.HasDerivedMethodAttribute(x => type.Value.entryPoint.Method.ExcludeAttributes.Contains(x.AttributeClass.ToString())))
                                            return false;
                                    }
                                }

                                return true;
                            }
                    }.ToImmutableHashSet() : ImmutableHashSet<ParameterMatcher>.Empty,
                    taintedMethods:
                        type.Value.source?.Methods
                            ?.Select<string, (MethodMatcher, ImmutableHashSet<string>)>(o =>
                                (
                                    (methodName, arguments) => methodName == o,
                                    ImmutableHashSet<string>.Empty.Add(TaintedTargetValue.Return)
                                ))
                            ?.ToImmutableHashSet()
                        ?? ImmutableHashSet<(MethodMatcher, ImmutableHashSet<string>)>.Empty,
                    taintedMethodsNeedsPointsToAnalysis:
                        ImmutableHashSet<(MethodMatcher, ImmutableHashSet<(PointsToCheck, string)>)>.Empty,
                    taintedMethodsNeedsValueContentAnalysis:
                        ImmutableHashSet<(MethodMatcher, ImmutableHashSet<(ValueContentCheck, string)>)>.Empty,
                    transferMethods:
                        (type.Value.sanitizer?.Methods != null || type.Value.transfer?.Methods != null)
                            ? (type.Value.sanitizer?.Methods ?? Enumerable.Empty<TransferInfo>())
                                .Where(method => method.InOut != null && method.InOut.Any(io => io.outArgumentName != TaintedTargetValue.Return))
                                .Concat(type.Value.transfer?.Methods ?? Enumerable.Empty<TransferInfo>())
                                .Select(method => new ValueTuple<MethodMatcher, (string, string)[]>
                                (
                                    (methodName, arguments) =>
                                    {
                                        if (methodName != method.Name)
                                            return false;

                                        if (method.ArgumentCount.HasValue && arguments.Length != method.ArgumentCount)
                                            return false;

                                        return true;
                                    },
                                    method.InOut.Where(pair => pair.outArgumentName != TaintedTargetValue.Return).ToArray()
                                )
                            )?.Select(o =>
                                (
                                    o.Item1,
                                    o.Item2
                                        ?.ToImmutableHashSet()
                                    ?? ImmutableHashSet<(string, string)>.Empty))
                            ?.ToImmutableHashSet()
                        ?? ImmutableHashSet<(MethodMatcher, ImmutableHashSet<(string, string)>)>.Empty : ImmutableHashSet<(MethodMatcher, ImmutableHashSet<(string, string)>)>.Empty,
                    taintConstantArray: false);

                    sourceInfosBuilder.Add(metadata);
            }

            return sourceInfosBuilder.ToImmutableAndFree();
        }

        private static (string, string)[] EmptyArgumentMatchArray = new (string, string)[0];

        private ImmutableHashSet<SanitizerInfo> GetSanitizerInfos(SinkKind sinkKind, Configuration config)
        {
            var duplicate = config.Sanitizers.Where(s => s.TaintTypes == null || s.TaintTypes.Any(x => (int)x == (int)sinkKind))
                                             .GroupBy(s => s.Type)
                                             .FirstOrDefault(x => x.Count() != 1);

            if (duplicate != null)
                throw new ArgumentException($"Conflicting sanitizers for '{duplicate.Key}'.");

            var sanitizerInfosBuilder = PooledHashSet<SanitizerInfo>.GetInstance();

            foreach (var sanitizer in config.Sanitizers.Where(s => s.TaintTypes == null || s.TaintTypes.Any(x => (int)x == (int)sinkKind)))
            {
                if (sanitizer.Methods.Any(x => x.ArgumentCount.HasValue || x.InOut != null))
                {
                    sanitizerInfosBuilder.AddSanitizerInfo(
                        sanitizer.Type,
                        isInterface: sanitizer.IsInterface ?? false,
                        isConstructorSanitizing: false,
                        sanitizingMethods: sanitizer.Methods.Select(method => new ValueTuple<MethodMatcher, (string, string)[]>
                            (
                                (methodName, arguments) =>
                                {
                                    if (methodName != method.Name)
                                        return false;

                                    if (method.ArgumentCount.HasValue && arguments.Length != method.ArgumentCount)
                                        return false;

                                    return true;
                                },
                                method.InOut ?? EmptyArgumentMatchArray
                            )
                        ),
                        sanitizingInstanceMethods: sanitizer.Methods.Any(m => m.CleansInstance == true)
                            ? sanitizer.Methods.Where(m => m.CleansInstance == true).Select(m => m.Name).ToArray()
                            : null
                    );
                }
                else
                {
                    sanitizerInfosBuilder.AddSanitizerInfo(
                        sanitizer.Type,
                        isInterface: sanitizer.IsInterface ?? false,
                        isConstructorSanitizing: false,
                        sanitizingMethods: sanitizer.Methods != null ? sanitizer.Methods.Select(x => x.Name).ToArray() : null,
                        sanitizingInstanceMethods: sanitizer.Methods.Any(m => m.CleansInstance == true)
                            ? sanitizer.Methods.Where(m => m.CleansInstance == true).Select(m => m.Name).ToArray()
                            : null);
                }
            }

            return sanitizerInfosBuilder.ToImmutableAndFree();
        }

        private ImmutableHashSet<SinkInfo> GetSinkInfos(SinkKind sinkKind, Configuration config)
        {
            var sinkInfosBuilder = PooledHashSet<SinkInfo>.GetInstance();

            foreach(var sink in config.Sinks.Where(s => s.TaintTypes.Any(x => (int)x == (int)sinkKind)))
            {
                sinkInfosBuilder.AddSinkInfo(
                    sink.Type,
                    sinkKind,
                    isInterface: sink.IsInterface ?? false,
                    isAnyStringParameterInConstructorASink: sink.IsAnyStringParameterInConstructorASink ?? false,
                    sinkProperties: sink.Properties ?? null,
                    sinkMethodParameters: sink.Methods != null ? sink.Methods.Select(x => (x.Key, x.Value)) : null);
            }

            return sinkInfosBuilder.ToImmutableAndFree();
        }
    }

    /// <summary>
    /// Internal configuration optimized for queries
    /// </summary>
    internal class Configuration
    {
        private Configuration()
        {
            _PasswordValidatorRequiredProperties = new HashSet<string>();
            PasswordValidatorRequiredProperties = new ReadOnlyHashSet<string>(_PasswordValidatorRequiredProperties);

            _CsrfGroupsList = new LinkedList<CsrfNamedGroup>();
            _CsrfGroups = new Dictionary<string, LinkedListNode<CsrfNamedGroup>>();
        }

        private readonly Lazy<TaintConfiguration> CachedTaintConfiguration;
        public TaintConfiguration TaintConfiguration { get { return CachedTaintConfiguration.Value; } }

        public Configuration(ConfigData configData, Compilation compilation) : this()
        {
            CachedTaintConfiguration = new Lazy<TaintConfiguration>(() => new TaintConfiguration(compilation, this));

            ReportAnalysisCompletion           = configData.ReportAnalysisCompletion           ?? false;
            AuditMode                          = configData.AuditMode                          ?? false;
            MinimumPasswordValidatorProperties = configData.MinimumPasswordValidatorProperties ?? 0;
            PasswordValidatorRequiredLength    = configData.PasswordValidatorRequiredLength    ?? 0;

            if (configData.PasswordValidatorRequiredProperties != null)
            {
                foreach (var data in configData.PasswordValidatorRequiredProperties)
                {
                    _PasswordValidatorRequiredProperties.Add(data);
                }
            }

            _TaintEntryPoints = configData.TaintEntryPoints;
            if (_TaintEntryPoints == null)
                _TaintEntryPoints = new Dictionary<string, TaintEntryPointData>();

            TaintEntryPoints = new ReadOnlyDictionary<string, TaintEntryPointData>(_TaintEntryPoints);

            _Sanitizers = configData.Sanitizers;
            if (_Sanitizers == null)
                _Sanitizers = new List<Sanitizer>();

            Sanitizers = new List<Sanitizer>(_Sanitizers);

            _Transfers = configData.Transfers;
            if (_Transfers == null)
                _Transfers = new List<Transfer>();

            Transfers = new List<Transfer>(_Transfers);

            _Sinks = configData.Sinks;
            if (_Sinks == null)
                _Sinks = new List<Sink>();

            Sinks = new List<Sink>(_Sinks);

            _TaintSources = configData.TaintSources;
            if (_TaintSources == null)
                _TaintSources = new List<TaintSource>();

            TaintSources = new List<TaintSource>(_TaintSources);

            if (configData.CsrfProtection != null)
            {
                foreach (var data in configData.CsrfProtection)
                {
                    AddCsrfProtectionToConfiguration(data.Value);
                }
            }

            if (configData.WebConfigFiles != null)
            {
                WebConfigFilesRegex = new Regex(configData.WebConfigFiles, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
        }

        public bool ReportAnalysisCompletion           { get; private set; }
        public bool AuditMode                          { get; private set; }
        public int  PasswordValidatorRequiredLength    { get; private set; }
        public int  MinimumPasswordValidatorProperties { get; private set; }

        private readonly HashSet<string>         _PasswordValidatorRequiredProperties;
        public           ReadOnlyHashSet<string> PasswordValidatorRequiredProperties { get; }

        private readonly Dictionary<string, TaintEntryPointData>          _TaintEntryPoints;
        public           IReadOnlyDictionary<string, TaintEntryPointData> TaintEntryPoints { get; }

        private readonly List<Sink>         _Sinks;
        public IReadOnlyList<Sink>          Sinks { get; }

        private readonly List<Sanitizer>    _Sanitizers;
        public IReadOnlyList<Sanitizer>     Sanitizers { get; }

        private readonly List<Transfer>     _Transfers;
        public IReadOnlyList<Transfer>      Transfers { get; }

        private readonly List<TaintSource>   _TaintSources;
        public IReadOnlyList<TaintSource>   TaintSources { get; }


        public Regex                        WebConfigFilesRegex { get; private set; }

        private readonly LinkedList<CsrfNamedGroup>                         _CsrfGroupsList; // ensure groups are exposed in the same order they were added
        private readonly Dictionary<string, LinkedListNode<CsrfNamedGroup>> _CsrfGroups;
        public IReadOnlyCollection<CsrfNamedGroup> CsrfGoups => _CsrfGroupsList;

        public void AddCsrfProtectionToConfiguration(CsrfProtectionData csrfData)
        {
            if (string.IsNullOrWhiteSpace(csrfData.Name))
                throw new Exception($"{nameof(CsrfProtectionData.Name)} is required in CsrfProtection");

            if (!_CsrfGroups.TryGetValue(csrfData.Name, out var curGroupNode))
            {
                var curGroup = new CsrfNamedGroup(csrfData);
                var node = _CsrfGroupsList.AddLast(curGroup);
                _CsrfGroups.Add(csrfData.Name, node);
            }
            else
            {
                curGroupNode.Value.AddFrom(csrfData);
            }
        }
    }
}
