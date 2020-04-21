﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Remote;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Rename
{
    public static class Renamer
    {
        public static Task<Solution> RenameSymbolAsync(
            Solution solution, ISymbol symbol, string newName, OptionSet optionSet, CancellationToken cancellationToken = default)
        {
            if (solution == null)
                throw new ArgumentNullException(nameof(solution));

            if (symbol == null)
                throw new ArgumentNullException(nameof(symbol));

            if (solution.GetOriginatingProjectId(symbol) == null)
                throw new ArgumentException(WorkspacesResources.Symbols_project_could_not_be_found_in_the_provided_solution, nameof(symbol));

            if (string.IsNullOrEmpty(newName))
                throw new ArgumentException(nameof(newName));

            optionSet ??= solution.Options;

            var renameOptions = RenameOptionSet.From(optionSet);
            return RenameSymbolAsync(solution, symbol, newName, renameOptions, nonConflictSymbols: null, cancellationToken);
        }

        internal static async Task<RenameLocations> FindRenameLocationsAsync(
            Solution solution, ISymbol symbol, RenameOptionSet options, CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(solution);
            Contract.ThrowIfNull(symbol);
            Contract.ThrowIfNull(solution.GetOriginatingProjectId(symbol), WorkspacesResources.Symbols_project_could_not_be_found_in_the_provided_solution);

            cancellationToken.ThrowIfCancellationRequested();

            using (Logger.LogBlock(FunctionId.Renamer_FindRenameLocationsAsync, cancellationToken))
            {
                var client = await RemoteHostClient.TryGetClientAsync(solution.Workspace, cancellationToken).ConfigureAwait(false);
                if (client != null)
                {
                    var result = await client.TryRunRemoteAsync<SerializableRenameLocations>(
                        WellKnownServiceHubServices.CodeAnalysisService,
                        nameof(IRemoteRenamer.FindRenameLocationsAsync),
                        solution,
                        new object[]
                        {
                            SerializableSymbolAndProjectId.Dehydrate(solution, symbol, cancellationToken),
                            SerializableRenameOptionSet.Dehydrate(options),
                        },
                        callbackTarget: null,
                        cancellationToken).ConfigureAwait(false);

                    if (result.HasValue)
                    {
                        return await RenameLocations.RehydrateAsync(
                            solution, result.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Couldn't effectively search in OOP. Perform the search in-proc.
            return await RenameLocations.FindLocationsInCurrentProcessAsync(
                symbol, solution, options, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<Solution> RenameAsync(
            RenameLocations locations,
            string newName,
            ImmutableHashSet<ISymbol> nonConflictSymbols = null,
            CancellationToken cancellationToken = default)
        {
            Contract.ThrowIfTrue(string.IsNullOrEmpty(newName));

            cancellationToken.ThrowIfCancellationRequested();

            var conflictResolution = await locations.ResolveConflictsAsync(
                newName, nonConflictSymbols, cancellationToken).ConfigureAwait(false);

            return conflictResolution.NewSolution;
        }

        internal static async Task<Solution> RenameSymbolAsync(
            Solution solution,
            ISymbol symbol,
            string newName,
            RenameOptionSet options,
            ImmutableHashSet<ISymbol> nonConflictSymbols,
            CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(solution);
            Contract.ThrowIfNull(symbol);
            Contract.ThrowIfNull(solution.GetOriginatingProjectId(symbol), WorkspacesResources.Symbols_project_could_not_be_found_in_the_provided_solution);
            Contract.ThrowIfTrue(string.IsNullOrEmpty(newName));

            cancellationToken.ThrowIfCancellationRequested();

            var renameLocations = await FindRenameLocationsAsync(solution, symbol, options, cancellationToken).ConfigureAwait(false);
            return await RenameAsync(renameLocations, newName, nonConflictSymbols, cancellationToken).ConfigureAwait(false);
        }
    }
}
