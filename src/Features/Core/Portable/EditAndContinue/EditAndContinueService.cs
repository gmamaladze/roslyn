﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.EditAndContinue
{
    /// <summary>
    /// Implements core of Edit and Continue orchestration: management of edit sessions and connecting EnC related services.
    /// </summary>
    /// <remarks>
    /// Although the service itself is host agnotic, some of the services it consumes are only available in particular hosts (like Visual Studio).
    /// Therefore this service doesn't export <see cref="IEditAndContinueService"/> on its own. Each host that supports EnC shall implement
    /// a subclass that exports <see cref="IEditAndContinueService"/>.
    /// </remarks>
    internal class EditAndContinueService : IEditAndContinueService
    {
        private readonly IActiveStatementProvider _activeStatementProvider;
        private readonly IDiagnosticAnalyzerService _diagnosticService;
        private DebuggingSession _debuggingSession;
        private EditSession _editSession;

        // Maps active statement instructions to their latest spans.
        //
        // Consider a function F containing a call to function G that is updated a couple of times
        // before the thread returns from G and is remapped to the latest version of F.
        // '>' indicates an active statement instruction.
        //
        // F v1:        F v2:       F v3:
        // 0: nop       0: nop      0: nop
        // 1> G()       1: nop      1: nop
        // 2: nop       2: G()      2: nop
        // 3: nop       3: nop      3> G()
        //
        // When entering a break state we query the debugger for current active statements.
        // The returned statements reflect the current state of the threads in the runtime.
        // When a change is successfully applied we remember changes in active statement spans.
        // These changes are passed to the next edit session.
        // We use them to map the spans for active statements returned by the debugger. 
        // 
        // In the above case the sequence of events is 
        // 1st break: get active statements returns (F, v=1, il=1, span1) the active statement is up-to-date
        // 1st apply: detected span change for active statement (F, v=1, il=1): span1->span2
        // 2nd break: previously updated statements contains (F, v=1, il=1)->span2
        //            get active statements returns (F, v=1, il=1, span1) which is mapped to (F, v=1, il=1, span2) using previously updated statements
        // 2nd apply: detected span change for active statement (F, v=1, il=1): span2->span3
        // 3rd break: previously updated statements contains (F, v=1, il=1)->span3
        //            get active statements returns (F, v=3, il=3, span3) the active statement is up-to-date
        //
        private Dictionary<ActiveInstructionId, LinePositionSpan> _lazyPreviouslyUpdatedActiveStatementSpans;

        internal EditAndContinueService(IDiagnosticAnalyzerService diagnosticService, IActiveStatementProvider activeStatementProvider)
        {
            Debug.Assert(diagnosticService != null);
            Debug.Assert(activeStatementProvider != null);

            _diagnosticService = diagnosticService;
            _activeStatementProvider = activeStatementProvider;
        }

        public DebuggingSession DebuggingSession => _debuggingSession;

        public EditSession EditSession => _editSession;

        public void StartDebuggingSession(Solution currentSolution)
        {
            Debug.Assert(_debuggingSession == null && _editSession == null);

            Interlocked.CompareExchange(ref _debuggingSession, new DebuggingSession(currentSolution), null);

            // TODO(tomat): allow changing documents
        }

        public void StartEditSession(
            Solution currentSolution,
            ImmutableDictionary<ProjectId, ProjectReadOnlyReason> projects,
            bool stoppedAtException)
        {
            Debug.Assert(_debuggingSession != null && _editSession == null);

            var newSession = new EditSession(
                currentSolution, 
                _debuggingSession, 
                _activeStatementProvider, 
                projects, 
                _lazyPreviouslyUpdatedActiveStatementSpans ?? SpecializedCollections.EmptyReadOnlyDictionary<ActiveInstructionId, LinePositionSpan>(),
                stoppedAtException);

            Interlocked.CompareExchange(ref _editSession, newSession, null);

            // TODO(tomat): allow changing documents
            // TODO(tomat): document added
        }

        public void EndEditSession(ImmutableArray<(ActiveInstructionId, LinePositionSpan)> updatedActiveStatementSpansOpt)
        {
            Debug.Assert(_debuggingSession != null && _editSession != null);

            // first, publish null session:
            var session = Interlocked.Exchange(ref _editSession, null);

            // then cancel all ongoing work bound to the session:
            session.Cancellation.Cancel();

            // then clear all reported rude edits:
            _diagnosticService.Reanalyze(_debuggingSession.InitialSolution.Workspace, documentIds: session.GetDocumentsWithReportedRudeEdits());

            // save updated active statement spans for the next edit session:
            if (!updatedActiveStatementSpansOpt.IsDefaultOrEmpty)
            {
                if (_lazyPreviouslyUpdatedActiveStatementSpans == null)
                {
                    _lazyPreviouslyUpdatedActiveStatementSpans = new Dictionary<ActiveInstructionId, LinePositionSpan>();
                }

                foreach (var (instruction, span) in updatedActiveStatementSpansOpt)
                {
                    _lazyPreviouslyUpdatedActiveStatementSpans[instruction] = span;
                }
            }

            // TODO(tomat): allow changing documents
        }

        public void EndDebuggingSession()
        {
            Debug.Assert(_debuggingSession != null && _editSession == null);
            _debuggingSession = null;
        }

        public bool IsProjectReadOnly(ProjectId id, out SessionReadOnlyReason sessionReason, out ProjectReadOnlyReason projectReason)
        {
            if (_debuggingSession == null)
            {
                projectReason = ProjectReadOnlyReason.None;
                sessionReason = SessionReadOnlyReason.None;
                return false;
            }

            // run mode - all documents that belong to the workspace shall be read-only:
            var editSession = _editSession;
            if (editSession == null)
            {
                projectReason = ProjectReadOnlyReason.None;
                sessionReason = SessionReadOnlyReason.Running;
                return true;
            }

            // break mode and stopped at exception - all documents shall be read-only:
            if (editSession.StoppedAtException)
            {
                projectReason = ProjectReadOnlyReason.None;
                sessionReason = SessionReadOnlyReason.StoppedAtException;
                return true;
            }

            // normal break mode - if the document belongs to a project that hasn't entered the edit session it shall be read-only:
            if (editSession.Projects.TryGetValue(id, out projectReason))
            {
                sessionReason = SessionReadOnlyReason.None;
                return projectReason != ProjectReadOnlyReason.None;
            }

            sessionReason = SessionReadOnlyReason.None;
            projectReason = ProjectReadOnlyReason.MetadataNotAvailable;
            return true;
        }

        public async Task<LinePositionSpan?> GetCurrentActiveStatementPositionAsync(ActiveInstructionId instructionId, CancellationToken cancellationToken)
        {
            try
            {
                // It is allowed to call this method before entering or after exiting break mode. In fact, the VS debugger does so. 
                // We return null since there the concept of active statement only makes sense during break mode.
                if (_editSession == null)
                {
                    return null;
                }

                Debug.Assert(_debuggingSession != null);

                var baseActiveStatements = await _editSession.BaseActiveStatements.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!baseActiveStatements.InstructionMap.TryGetValue(instructionId, out var baseActiveStatement))
                {
                    return null;
                }

                Document primaryDocument = _debuggingSession.InitialSolution.Workspace.CurrentSolution.GetDocument(baseActiveStatement.PrimaryDocumentId);

                // TODO:is this needed?
                // Try to get spans from the tracking service first.
                // We might get an imprecise result if the document analysis hasn't been finished yet and 
                // the active statement has structurally changed, but that's ok. The user won't see an updated tag
                // for the statement until the analysis finishes anyways.
                //
                // SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                //if (_trackingService.TryGetSpan(baseActiveStatement.Id, text, out var trackedSpan) && trackedSpan.Length > 0)
                //{
                //    lineSpan = text.Lines.GetLinePositionSpan(trackedSpan);
                //}

                var documentAnalysis = await _editSession.GetDocumentAnalysis(primaryDocument).GetValueAsync(cancellationToken).ConfigureAwait(false);
                var currentActiveStatements = documentAnalysis.ActiveStatements;
                if (currentActiveStatements.IsDefault)
                {
                    // The document has syntax errors.
                    return null;
                }

                return currentActiveStatements[baseActiveStatement.PrimaryDocumentOrdinal].Span;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrashUnlessCanceled(e))
            {
                return null;
            }
        }

        public async Task<bool?> IsActiveStatementInExceptionRegionAsync(ActiveInstructionId instructionId, CancellationToken cancellationToken)
        {
            try
            {
                if (_editSession == null)
                {
                    return null;
                }

                Debug.Assert(_debuggingSession != null);

                var baseActiveStatements = await _editSession.BaseActiveStatements.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!baseActiveStatements.InstructionMap.TryGetValue(instructionId, out var baseActiveStatement))
                {
                    return null;
                }

                // TODO: avoid waiting for ERs of all active statements to be calculated and just calculate the one we are interested in at this moment:
                var baseExceptionRegions = await _editSession.BaseActiveExceptionRegions.GetValueAsync(cancellationToken).ConfigureAwait(false);
                return baseExceptionRegions[baseActiveStatement.Ordinal].IsActiveStatementCovered;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrashUnlessCanceled(e))
            {
                return null;
            }
        }
    }
}
