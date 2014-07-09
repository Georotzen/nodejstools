﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.NodejsTools.Parsing;

namespace Microsoft.NodejsTools.Formatting {
    sealed class FormattingVisitor : AstVisitor {
        private readonly string _code;
        private readonly FormattingOptions _options;
        private readonly List<Edit> _edits = new List<Edit>();
        private readonly List<string> _whitespace = new List<string>();
        private readonly bool _onEnter;
        private int _indentLevel;

        // various terminators when we're replacing formatting
        private static char[] _semicolon = new[] { ';' };
        private static char[] _newlines = new[] { '\n', '\r' };
        private static char[] _comma = new[] { ',' };
        private static char[] _openParen = new[] { '(' };
        private static char[] _closeBrace = new[] { '}' };

        public FormattingVisitor(string code, FormattingOptions options = null, bool onEnter = false) {
            _code = code;
            _options = options ?? new FormattingOptions();
            _onEnter = onEnter;
        }

        public void Format(JsAst ast) {
            RemoveTrailingWhiteSpace(ast.StartIndex);
            WalkStatements(ast, ast.Block.Statements, false);
            if (ast.Block.Count > 0) {
                FixStatementIndentation(ast.Block[ast.Block.Count - 1].EndIndex, ast.EndIndex);
            }
            ReplacePreceedingWhiteSpace(ast.EndIndex);
        }

        public List<Edit> Edits {
            get {
                return _edits;
            }
        }

        #region Complex Statements

        public override bool Walk(ForNode node) {
            ReplaceControlFlowWhiteSpace(node, "for".Length);

            if (node.Initializer != null) {
                ReplacePreceedingWhiteSpace(
                    node.Initializer.StartIndex,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : "",
                    _openParen
                );

                node.Initializer.Walk(this);
            }

            if (node.Condition != null) {
                ReplacePreceedingWhiteSpace(
                    node.Condition.StartIndex,
                    _options.SpaceAfterSemiColonInFor ? " " : "",
                    _semicolon
                );

                node.Condition.Walk(this);
            }
            if (node.Incrementer != null) {
                ReplacePreceedingWhiteSpace(node.Incrementer.StartIndex, _options.SpaceAfterSemiColonInFor ? " " : "", _semicolon);
                node.Incrementer.Walk(this);

                ReplaceFollowingWhiteSpace(
                    node.Incrementer.EndIndex,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
                );
            }

            if (node.HeaderEnd != -1) {
                WalkFlowControlBlockWithOptionalParens(node.Body, node.HeaderEnd, false);
            }
            return false;
        }

        private void WalkFlowControlBlockWithOptionalParens(Block block, int previousExpressionEnd, bool inParens) {
            WalkFlowControlBlockWithOptionalParens(block, ((Statement)block.Parent).StartIndex, previousExpressionEnd, inParens);
        }

        private void WalkFlowControlBlockWithOptionalParens(Block block, int startIndex, int previousExpressionEnd, bool inParens) {
            if (block != null) {
                if (block.Braces == BraceState.None) {
                    // braces are omitted...

                    // if (foo) 
                    //      blah
                    // vs
                    // if (foo) blah

                    bool multiLine = ContainsLineFeed(previousExpressionEnd, block.StartIndex);
                    if (multiLine) {
                        // remove trailing whitespace at the end of this line
                        bool followedBySingleLineComment;
                        int startOfWhiteSpace, whiteSpaceCount;
                        ParseEndOfLine(previousExpressionEnd, inParens, out followedBySingleLineComment, out startOfWhiteSpace, out whiteSpaceCount);
                        if (startOfWhiteSpace != -1) {
                            _edits.Add(new Edit(startOfWhiteSpace, whiteSpaceCount, ""));
                        }
                        Indent();
                    }

                    WalkStatements(startIndex, block.Statements, false);
                    if (multiLine) {
                        Dedent();
                    }
                } else {
                    ReplacePreceedingIncludingNewLines(block.StartIndex, GetFlowControlBraceInsertion(previousExpressionEnd, false));

                    WalkBlock(block);
                }
            }
        }

        public override bool Walk(ForIn node) {
            ReplaceControlFlowWhiteSpace(node, "for".Length);

            ReplacePreceedingWhiteSpace(
                node.Variable.StartIndex,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : "",
                _openParen
            );
            node.Variable.Walk(this);

            ReplaceFollowingWhiteSpace(
                node.Variable.EndIndex,
                " "
            );

            ReplacePreceedingWhiteSpace(
                node.Collection.StartIndex,
                " "
            );

            node.Collection.Walk(this);

            ReplaceFollowingWhiteSpace(
                node.Collection.EndIndex,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
            );

            WalkFlowControlBlockWithOptionalParens(node.Body, node.Collection.EndIndex, true);
            return false;
        }

        public override bool Walk(IfNode node) {
            ReplaceControlFlowWhiteSpace(node, "if".Length);

            EnsureSpacesAroundParenthesisedExpression(node.Condition);

            WalkFlowControlBlockWithOptionalParens(node.TrueBlock, node.Condition.EndIndex, true);
            if (node.FalseBlock != null) {
                ReplacePreceedingWhiteSpaceMaybeMultiline(node.ElseStart);
                WalkFlowControlBlockWithOptionalParens(node.FalseBlock, node.ElseStart, node.ElseStart + "else".Length, false);
            }
            return false;
        }

        public override bool Walk(TryNode node) {
            ReplacePreceedingIncludingNewLines(node.TryBlock.StartIndex, GetFlowControlBraceInsertion(node.StartIndex + "try".Length, false));
            WalkBlock(node.TryBlock);
            if (node.CatchParameter != null) {
                if (node.CatchStart != -1) {
                    ReplacePreceedingWhiteSpace(node.CatchStart, " ", _closeBrace);
                    ReplaceFollowingWhiteSpace(node.CatchStart + "catch".Length, " ");
                }

                ReplacePreceedingWhiteSpace(
                    node.CatchParameter.StartIndex,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : "",
                    _openParen
                );

                ReplaceFollowingWhiteSpace(
                    node.CatchParameter.EndIndex,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
                );

                ReplacePreceedingIncludingNewLines(node.CatchBlock.StartIndex, GetFlowControlBraceInsertion(node.CatchParameter.EndIndex, true));
                WalkBlock(node.CatchBlock);
            }

            if (node.FinallyBlock != null && node.FinallyStart != -1) {
                if (node.FinallyStart != -1) {
                    ReplacePreceedingWhiteSpace(node.FinallyStart, " ", _closeBrace);
                }
                ReplacePreceedingIncludingNewLines(node.FinallyBlock.StartIndex, GetFlowControlBraceInsertion(node.FinallyStart + "finally".Length, false));
                WalkBlock(node.FinallyBlock);
            }
            return false;
        }

        public override bool Walk(Microsoft.NodejsTools.Parsing.Switch node) {
            ReplaceControlFlowWhiteSpace(node, "switch".Length);

            EnsureSpacesAroundParenthesisedExpression(node.Expression);
            bool isMultiLine = false;
            if (node.BlockStart != -1) {
                ReplacePreceedingIncludingNewLines(node.BlockStart, GetFlowControlBraceInsertion(node.Expression.EndIndex, true));

                isMultiLine = ContainsLineFeed(node.BlockStart, node.EndIndex);
            }

            // very similar to walking a block w/o a block
            Indent();
            for (int i = 0; i < node.Cases.Count; i++) {

                var caseNode = node.Cases[i];

                if (i == 0 && isMultiLine) {
                    EnsureNewLinePreceeding(caseNode.StartIndex);
                } else {
                    ReplacePreceedingWhiteSpace(caseNode.StartIndex);
                }

                if (caseNode.CaseValue != null) {
                    ReplacePreceedingWhiteSpace(caseNode.CaseValue.StartIndex, " ");
                    caseNode.CaseValue.Walk(this);
                    ReplaceFollowingWhiteSpace(caseNode.CaseValue.EndIndex, "");
                }

                if (caseNode.ColonIndex != -1 &&
                    caseNode.Statements.Count > 0 &&
                    ContainsLineFeed(caseNode.ColonIndex, caseNode.Statements[0].StartIndex)) {
                    ReplaceFollowingWhiteSpace(caseNode.ColonIndex + ":".Length, "");
                }

                bool indent = caseNode.Statements.Count == 0 ||
                              ShouldIndentForChild(caseNode, caseNode.Statements[0]);
                if (indent) {
                    Indent();
                }
                WalkStatements(
                    caseNode,
                    caseNode.Statements.Statements,
                    false
                );
                if (indent) {
                    Dedent();
                }
            }
            Dedent();

            ReplacePreceedingWhiteSpace(node.EndIndex - 1);

            return false;
        }

        public override bool Walk(DoWhile node) {
            WalkFlowControlBlockWithOptionalParens(node.Body, node.StartIndex + "do".Length, false);

            ReplaceFollowingWhiteSpace(node.Body.EndIndex, " ");

            EnsureSpacesAroundParenthesisedExpression(node.Condition);

            return false;
        }

        public override bool Walk(WhileNode node) {
            ReplaceControlFlowWhiteSpace(node, "while".Length);

            EnsureSpacesAroundParenthesisedExpression(node.Condition);

            WalkFlowControlBlockWithOptionalParens(node.Body, node.Condition.EndIndex, true);
            return false;
        }

        public override bool Walk(WithNode node) {
            ReplaceControlFlowWhiteSpace(node, "with".Length);

            EnsureSpacesAroundParenthesisedExpression(node.WithObject);

            WalkFlowControlBlockWithOptionalParens(node.Body, node.WithObject.EndIndex, true);
            return false;
        }

        public override bool Walk(FunctionObject node) {
            if (node.Name == null) {
                ReplaceFollowingWhiteSpace(
                    node.StartIndex + "function".Length,
                    _options.SpaceAfterFunctionInAnonymousFunctions ? " " : ""
                );
            } else {
                ReplaceFollowingWhiteSpace(
                    node.NameSpan.End,
                    ""
                );
            }

            if (node.ParameterDeclarations.Count > 0) {
                ReplaceFollowingWhiteSpace(
                    node.ParameterStart + 1,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
                );

                for (int i = 1; i < node.ParameterDeclarations.Count; i++) {
                    ReplacePreceedingWhiteSpace(node.ParameterDeclarations[i].StartIndex, _options.SpaceAfterComma ? " " : "", _comma);
                }

                ReplacePreceedingWhiteSpace(
                    node.ParameterEnd - 1,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
                );
            } else {
                ReplaceFollowingWhiteSpace(
                    node.ParameterStart + 1,
                    ""
                );
            }

            if (!_onEnter) {
                ReplacePreceedingIncludingNewLines(
                    node.Body.StartIndex,
                    _options.OpenBracesOnNewLineForFunctions || FollowedBySingleLineComment(node.ParameterEnd, false) ?
                        ReplaceWith.InsertNewLineAndIndentation :
                        ReplaceWith.InsertSpace
                );
            }

            WalkBlock(node.Body);

            return false;
        }

        public override bool Walk(Block node) {
            Debug.Assert(node.Braces != BraceState.None);
            WalkBlock(node);
            return false;
        }

        /// <summary>
        /// Replaces the whitespace for a control flow node.  Updates the current indentation
        /// level and then updates the whitespace after the keyword based upon the format
        /// options.
        /// </summary>
        private void ReplaceControlFlowWhiteSpace(Statement node, int keywordLength) {
            ReplaceFollowingWhiteSpace(node.StartIndex + keywordLength, _options.SpaceAfterKeywordsInControlFlowStatements ? " " : "");
        }

        #endregion

        #region Simple Statements

        public override bool Walk(LabeledStatement node) {
            if (node.Statement != null) {
                // don't indent block statements that start on the same line
                // as the label such as:
                // label: {
                //      code
                // }
                var block = node.Statement;

                bool indent = ShouldIndentForChild(node, block);
                if (indent) {
                    Indent();
                }
                ReplacePreceedingWhiteSpaceMaybeMultiline(node.Statement.StartIndex);
                node.Statement.Walk(this);
                if (indent) {
                    Dedent();
                }
            }
            return false;
        }

        private bool ShouldIndentForChild(Statement parent, Statement block) {
            // if the child is a block which has braces and starts on the
            // same line as our parent we don't want to indent, for example:
            // case foo: {
            //     code
            // }
            return !(block is Block &&
                ((Block)block).Braces != BraceState.None &&
                !ContainsLineFeed(parent.StartIndex, block.StartIndex));
        }

        public override bool Walk(Break node) {
            if (node.Label != null) {
                ReplaceFollowingWhiteSpace(node.StartIndex + "break".Length, " ");
            }
            RemoveSemiColonWhiteSpace(node.EndIndex);
            return base.Walk(node);
        }

        /// <summary>
        /// Removes any white space proceeding a semi colon
        /// </summary>
        /// <param name="endIndex"></param>
        private void RemoveSemiColonWhiteSpace(int endIndex) {
            if (_code[endIndex - 1] == ';') {
                ReplacePreceedingWhiteSpace(endIndex - 1, "");
            }
        }

        public override bool Walk(ContinueNode node) {
            if (node.Label != null) {
                ReplaceFollowingWhiteSpace(node.StartIndex + "continue".Length, " ");
            }
            RemoveSemiColonWhiteSpace(node.EndIndex);
            return base.Walk(node);
        }

        public override bool Walk(DebuggerNode node) {
            ReplaceFollowingWhiteSpace(node.StartIndex + "debugger".Length, "");
            return base.Walk(node);
        }

        public override bool Walk(Var node) {
            ReplaceFollowingWhiteSpace(node.StartIndex + "var".Length, " ");

            if (node.Count > 0) {

                FormatVariableDeclaration(node[0]);

                IndentSpaces(4);
                for (int i = 1; i < node.Count; i++) {
                    var curDecl = node[i];

                    ReplacePreceedingWhiteSpaceMaybeMultiline(curDecl.NameSpan.Start);

                    FormatVariableDeclaration(curDecl);
                }
                DedentSpaces(4);

                if (!node[node.Count - 1].HasInitializer) {
                    // if we have an initializer the whitespace was
                    // cleared between the end of the initializer and the
                    // semicolon
                    RemoveSemiColonWhiteSpace(node.EndIndex);
                }
            }

            return false;
        }

        public override bool Walk(ReturnNode node) {
            if (node.Operand != null) {
                ReplaceFollowingWhiteSpace(node.StartIndex + "return".Length, " ");
                node.Operand.Walk(this);
            }
            RemoveSemiColonWhiteSpace(node.EndIndex);
            return false;
        }

        public override bool Walk(ExpressionStatement node) {
            node.Expression.Walk(this);
            RemoveSemiColonWhiteSpace(node.EndIndex);
            return false;
        }

        public override bool Walk(ThrowNode node) {
            if (node.Operand != null) {
                ReplaceFollowingWhiteSpace(node.StartIndex + "throw".Length, " ");
            }
            RemoveSemiColonWhiteSpace(node.EndIndex);
            return base.Walk(node);
        }

        #endregion

        #region Expressions

        public override bool Walk(ObjectLiteralProperty node) {
            if (node.Name is GetterSetter) {
                ReplaceFollowingWhiteSpace(node.Name.StartIndex + "get".Length, " ");
                node.Value.Walk(this);
            } else {
                node.Name.Walk(this);
                ReplacePreceedingWhiteSpace(node.Value.StartIndex, " ");
                node.Value.Walk(this);
            }
            return false;
        }

        public override bool Walk(ObjectLiteral node) {
            if (node.Properties.Count == 0) {
                ReplacePreceedingWhiteSpace(node.EndIndex - 1, "");
            } else {
                Indent();
                bool isMultiLine = ContainsLineFeed(node.StartIndex, node.EndIndex);
                if (node.Properties.Count > 0) {
                    if (isMultiLine) {
                        // multiline block statement, make sure the 1st statement
                        // starts on a new line
                        EnsureNewLineFollowing(node.StartIndex + "{".Length);
                    }

                    WalkStatements(node, node.Properties, isMultiLine);
                }
                Dedent();

                if (isMultiLine) {
                    ReplacePreceedingIncludingNewLines(node.EndIndex - 1, ReplaceWith.InsertNewLineAndIndentation);
                } else {
                    ReplacePreceedingWhiteSpace(node.EndIndex - 1, " ");
                }
            }
            return false;
        }

        public override bool Walk(GroupingOperator node) {
            ReplaceFollowingWhiteSpace(
                node.StartIndex + 1,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
            );

            node.Operand.Walk(this);

            ReplacePreceedingWhiteSpace(
                node.EndIndex - 1,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
            );

            return false;
        }

        public override bool Walk(Member node) {
            node.Root.Walk(this);
            ReplaceFollowingWhiteSpace(node.Root.EndIndex, "");
            ReplaceFollowingWhiteSpace(node.NameSpan.Start + 1, "");
            return false;
        }

        public override bool Walk(CallNode node) {
            node.Function.Walk(this);

            if (node.IsConstructor) {
                ReplaceFollowingWhiteSpace(node.StartIndex + "new".Length, " ");
            }

            if (!node.InBrackets) {
                ReplaceFollowingWhiteSpace(
                    node.Function.EndIndex,
                    ""
                );
            }

            if (node.Arguments != null && node.Arguments.Count > 0) {
                ReplacePreceedingWhiteSpace(
                    node.Arguments[0].StartIndex,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : "",
                    _openParen
                );

                node.Arguments[0].Walk(this);

                for (int i = 1; i < node.Arguments.Count; i++) {
                    ReplacePreceedingWhiteSpace(
                        node.Arguments[i].StartIndex,
                        _options.SpaceAfterComma ? " " : "",
                        _comma
                    );

                    node.Arguments[i].Walk(this);
                }
            }

            if (!node.InBrackets) {
                ReplacePreceedingWhiteSpace(
                    node.EndIndex - 1,
                    _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
                );
            }

            return false;
        }

        public override bool Walk(CommaOperator node) {
            if (node.Expressions != null && node.Expressions.Length != 0) {
                node.Expressions[0].Walk(this);

                for (int i = 1; i < node.Expressions.Length; i++) {
                    ReplacePreceedingWhiteSpace(node.Expressions[i].StartIndex, _options.SpaceAfterComma ? " " : "", _comma);
                    node.Expressions[i].Walk(this);
                }
            }
            return false;
        }

        public override bool Walk(UnaryOperator node) {
            if (!node.IsPostfix) {
                if (node.OperatorToken == JSToken.Void ||
                    node.OperatorToken == JSToken.TypeOf ||
                    node.OperatorToken == JSToken.Delete) {
                    ReplacePreceedingWhiteSpace(node.Operand.StartIndex, " ");
                } else {
                    ReplacePreceedingWhiteSpace(node.Operand.StartIndex, "");
                }
            }
            node.Operand.Walk(this);
            if (node.IsPostfix) {
                ReplaceFollowingWhiteSpace(node.Operand.EndIndex, "");
            }
            return false;
        }

        public override bool Walk(BinaryOperator node) {
            node.Operand1.Walk(this);

            ReplaceFollowingWhiteSpace(
                node.Operand1.EndIndex,
                _options.SpaceBeforeAndAfterBinaryOperator ? " " : null
            );

            ReplacePreceedingWhiteSpace(
                node.Operand2.StartIndex,
                _options.SpaceBeforeAndAfterBinaryOperator ? " " : null,
                null,
                _newlines
            );

            node.Operand2.Walk(this);

            return false;
        }

        #endregion

        #region Formatting Infrastructure

        private bool ContainsLineFeed(int start, int end) {
            return _code.IndexOfAny(_newlines, start, end - start) != -1;
        }


        /// <summary>
        /// Parses the end of the line getting the range of trailing whitespace and if the line is terminated with
        /// a single line comment.
        /// </summary>
        private void ParseEndOfLine(int startIndex, bool inParens, out bool followedbySingleLineComment, out int startOfTerminatingWhiteSpace, out int whiteSpaceCount) {
            followedbySingleLineComment = false;
            startOfTerminatingWhiteSpace = -1;
            whiteSpaceCount = 0;
            for (int i = startIndex; i < _code.Length; i++) {
                if (_code[i] == ' ' || _code[i] == '\t') {
                    if (!inParens && startOfTerminatingWhiteSpace == -1) {
                        startOfTerminatingWhiteSpace = i;
                        whiteSpaceCount = 0;
                    }
                    whiteSpaceCount++;
                    continue;
                } else if (inParens && _code[i] == ')') {
                    // we were in a parenthesised expression, now we're out
                    // of it and can continue scanning for the single line
                    // comment
                    inParens = false;
                    continue;
                } else if (_code[i] == '\r' || _code[i] == '\n') {
                    if (!inParens && startOfTerminatingWhiteSpace == -1) {
                        startOfTerminatingWhiteSpace = i;
                        whiteSpaceCount = 0;
                    }
                    return;
                } else if (_code[i] == '/') {
                    if (i + 1 < _code.Length) {
                        if (_code[i + 1] == '/') {
                            followedbySingleLineComment = true;
                            startOfTerminatingWhiteSpace = -1;
                            return;
                        } else if (_code[i + 1] == '*') {
                            // need to skip this comment
                            int endComment = _code.IndexOf("*/", i + 2);
                            if (endComment == -1 || ContainsLineFeed(i + 2, endComment)) {
                                startOfTerminatingWhiteSpace = -1;
                                return;
                            }

                            i = endComment + 1;
                            continue;
                        }
                    }
                } else {
                    startOfTerminatingWhiteSpace = -1;
                }
            }
        }

        private bool FollowedBySingleLineComment(int startIndex, bool inParens) {
            bool followedBySingleLineComment;
            int startOfWhiteSpace, whiteSpaceCount;
            ParseEndOfLine(startIndex, inParens, out followedBySingleLineComment, out startOfWhiteSpace, out whiteSpaceCount);
            return followedBySingleLineComment;
        }

        private void IndentSpaces(int spaces) {
            if (_options.SpacesPerIndent == null) {
                Indent();
            } else {
                for (int i = 0; i < spaces / _options.SpacesPerIndent.Value; i++) {
                    Indent();
                }
            }
        }

        private void DedentSpaces(int spaces) {
            if (_options.SpacesPerIndent == null) {
                Dedent();
            } else {
                for (int i = 0; i < spaces / _options.SpacesPerIndent.Value; i++) {
                    Dedent();
                }
            }
        }

        private void FormatVariableDeclaration(VariableDeclaration curDecl) {
            if (curDecl.HasInitializer) {
                ReplaceFollowingWhiteSpace(
                    curDecl.NameSpan.End,
                    _options.SpaceBeforeAndAfterBinaryOperator ? " " : ""
                );

                ReplacePreceedingWhiteSpace(
                    curDecl.Initializer.StartIndex,
                    _options.SpaceBeforeAndAfterBinaryOperator ? " " : ""
                );

                curDecl.Initializer.Walk(this);

                ReplaceFollowingWhiteSpace(
                    curDecl.Initializer.EndIndex,
                    ""
                );
            }
        }

        private string GetIndentation() {
            if (_indentLevel < _whitespace.Count &&
                _whitespace[_indentLevel] != null) {
                return _whitespace[_indentLevel];
            }

            while (_indentLevel >= _whitespace.Count) {
                _whitespace.Add(null);
            }

            if (_options.SpacesPerIndent != null) {
                return _whitespace[_indentLevel] = new string(
                    ' ',
                    _indentLevel * _options.SpacesPerIndent.Value
                );
            }

            return _whitespace[_indentLevel] = new string('\t', _indentLevel);
        }

        private void Indent() {
            _indentLevel++;
        }

        private void Dedent() {
            _indentLevel--;
        }

        enum ReplaceWith {
            None,
            InsertNewLineAndIndentation,
            InsertSpace,
        }

        private string GetBraceNewLineFormatting(ReplaceWith format) {
            switch (format) {
                case ReplaceWith.InsertNewLineAndIndentation:
                    return _options.NewLine + GetIndentation();
                case ReplaceWith.InsertSpace:
                    return " ";
                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Reformats a block node.  If the block has braces then the formatting will be updated
        /// appropriately, if forceNewLine
        /// </summary>
        /// <param name="block"></param>
        /// <param name="braceOnNewline"></param>
        private void WalkBlock(Block block) {
            Debug.Assert(block == null || block.Braces != BraceState.None);
            if (block != null && block.Braces != BraceState.None) {
                bool isMultiLine = ContainsLineFeed(block.StartIndex, block.EndIndex);
                if (block.Count > 0 && isMultiLine) {
                    // multiline block statement, make sure the 1st statement
                    // starts on a new line
                    EnsureNewLineFollowing(block.StartIndex + "{".Length);
                }

                var parent = block.Parent;
                Indent();

                WalkStatements(block, block.Statements, isMultiLine);

                Dedent();

                if (block.Braces == BraceState.StartAndEnd) {
                    if (isMultiLine) {
                        EnsureNewLinePreceeding(block.EndIndex - 1);
                    } else {
                        ReplacePreceedingWhiteSpaceMaybeMultiline(block.EndIndex - 1);
                    }
                }
            }
        }

        private void WalkStatements(Node node, IEnumerable<Node> stmts, bool isMultiLine) {
            WalkStatements(node.StartIndex, stmts, isMultiLine);
        }

        private void WalkStatements(int startIndex, IEnumerable<Node> stmts, bool isMultiLine) {
            int prevStart = startIndex;
            int i = 0;
            foreach (var curStmt in stmts) {
                if (i == 0 && isMultiLine && !ContainsLineFeed(startIndex, curStmt.StartIndex)) {
                    // force a newline before the 1st statement begins
                    ReplacePreceedingWhiteSpace(curStmt.StartIndex, terminators: null);
                } else {
                    // fix up whitespace for any interleaving blank / comment lines
                    if (!FixStatementIndentation(prevStart, curStmt.StartIndex)) {
                        if (curStmt is EmptyStatement) {
                            // if (blah); shouldn't get a space...
                            ReplacePreceedingWhiteSpace(curStmt.StartIndex, null);
                        } else {
                            ReplacePreceedingWhiteSpaceMaybeMultiline(curStmt.StartIndex);
                        }
                    }
                }

                curStmt.Walk(this);

                RemoveTrailingWhiteSpace(curStmt.EndIndex);

                prevStart = curStmt.EndIndex;
                i++;
            }
        }

        private bool FixStatementIndentation(int prevStart, int end) {
            bool newlines = false;
            int newLine;
            while ((newLine = _code.IndexOfAny(_newlines, prevStart, end - prevStart)) != -1) {
                bool endsInSingleLineComment;
                int startTerminatingWhiteSpace, whiteSpaceCount;
                ParseEndOfLine(prevStart, false, out endsInSingleLineComment, out startTerminatingWhiteSpace, out whiteSpaceCount);
                if (!endsInSingleLineComment && startTerminatingWhiteSpace == -1) {
                    // don't fix up white space in lines with comments
                    break;
                }
                newlines = true;
                if (_code[newLine] == '\n' ||
                    (_code[newLine] == '\r' &&
                    newLine != _code.Length - 1 &&
                    _code[newLine + 1] != '\n')) {
                    prevStart = newLine + 1;
                } else {
                    prevStart = newLine + 2;
                }
                ReplaceFollowingWhiteSpace(prevStart, GetIndentation());
            }
            return newlines;
        }

        private void EnsureNewLineFollowing(int start) {
            for (int i = start; i < _code.Length; i++) {
                if (_code[i] == ' ' || _code[i] == '\t') {
                    continue;
                } else if (_code[i] == '\r') {
                    if (i + 1 < _code.Length && _code[i + 1] == '\n') {
                        MaybeReplaceText(
                            start,
                            i + 2,
                            _options.NewLine
                        );
                    } else {
                        MaybeReplaceText(
                            start,
                            i + 1,
                            _options.NewLine
                        );
                    }
                    return;
                } else if (_code[i] == '\n') {
                    MaybeReplaceText(
                        start,
                        i + 1,
                        _options.NewLine
                    );
                    return;
                } else {
                    MaybeReplaceText(
                        start,
                        start,
                        _options.NewLine
                    );
                    return;
                }
            }

            MaybeReplaceText(_code.Length, _code.Length, _options.NewLine);
        }

        private void EnsureNewLinePreceeding(int start) {
            for (int i = start - 1; i >= 0; i--) {
                if (_code[i] == ' ' || _code[i] == '\t') {
                    continue;
                } else if (_code[i] == '\n') {
                    if (i >= 1 && _code[i - 1] == '\r') {
                        MaybeReplaceText(
                            i - 1,
                            start,
                            _options.NewLine + GetIndentation()
                        );
                        break;
                    }
                    MaybeReplaceText(
                        i,
                        start,
                        _options.NewLine + GetIndentation()
                    );
                    break;
                } else if (_code[i] == '\r') {
                    MaybeReplaceText(
                        i,
                        start,
                        _options.NewLine + GetIndentation()
                    );
                    break;
                } else {
                    MaybeReplaceText(
                        i + 1,
                        start,
                        _options.NewLine + GetIndentation()
                    );
                    break;
                }
            }
        }

        private void ReplacePreceedingIncludingNewLines(int start, ReplaceWith braceOnNewline) {
            int codeIndex;
            for (codeIndex = start - 1; codeIndex >= 0; codeIndex--) {
                if (_code[codeIndex] == '\r' || _code[codeIndex] == '\n') {
                    // new lines are always ok to replace...
                    continue;
                } else if (_code[codeIndex] == ' ' || _code[codeIndex] == '\t') {
                    // spaces are ok as long as we're not just trying to fix up newlines...
                    continue;
                } else {
                    // hit a newline, replace the indentation with new indentation
                    MaybeReplaceText(
                        codeIndex + 1,
                        start,
                        GetBraceNewLineFormatting(braceOnNewline)
                    );
                    break;
                }
            }
            if (codeIndex == -1) {
                MaybeReplaceText(
                    0,
                    start,
                    GetBraceNewLineFormatting(braceOnNewline)
                );
            }
        }

        /// <summary>
        /// Gets the brace insertion style for a control flow keyword which
        /// is followed by an expression and then the brace.
        /// </summary>
        /// <returns></returns>
        private ReplaceWith GetFlowControlBraceInsertion(int previousExpressionEnd, bool inParens) {
            // By default we follow the option, but if we have code like:

            // if(x) // comment
            // {

            // Then we need to force/keep the brace on the next line with proper indentation

            if (_options.OpenBracesOnNewLineForControl ||
                FollowedBySingleLineComment(previousExpressionEnd, inParens)) {
                return ReplaceWith.InsertNewLineAndIndentation;
            }

            return ReplaceWith.InsertSpace;
        }

        private void ReplaceFollowingWhiteSpace(int startIndex, string whiteSpace) {
            for (int i = startIndex; i < _code.Length; i++) {
                if (_code[i] != ' ' && _code[i] != '\t') {
                    MaybeReplaceText(startIndex, i, whiteSpace);
                    break;
                }
            }
        }

        private void RemoveTrailingWhiteSpace(int startIndex) {
            for (int i = startIndex; i < _code.Length; i++) {
                if (_code[i] == ' ' || _code[i] == '\t') {
                    continue;
                } else if (_code[i] == '\r' || _code[i] == '\n') {
                    MaybeReplaceText(startIndex, i, "");
                    break;
                } else {
                    break;
                }
            }
        }

        /// <summary>
        /// Replaces the whitespace in from of start with the current indentation level
        /// if it terminates at a newline character.
        /// </summary>
        private void ReplacePreceedingWhiteSpace(int start) {
            ReplacePreceedingWhiteSpace(start, terminators: _newlines);
        }

        /// <summary>
        /// Replaces the preceeding whitespace characters with the current indentation or specified whitespace.
        /// 
        /// If terminators are provided then one of the specified characters must be encountered
        /// to do the replacement.  Otherwise if any other non-whitespace character is 
        /// encountered then the replacement will not occur.
        /// </summary>
        /// <param name="start">the starting position to search backwards from to replace</param>
        /// <param name="newWhiteSpace">The new whitespace or null to use the current indentation</param>
        /// <param name="terminators">null to replace when any non-whitespace character is encountered or 
        /// a list of characters which must be encountered to do the replacement.</param>
        private void ReplacePreceedingWhiteSpace(int start, string newWhiteSpace = null, char[] terminators = null, char[] abortTerminators = null) {
            int codeIndex;
            for (codeIndex = start - 1; codeIndex >= 0; codeIndex--) {
                if (_code[codeIndex] == ' ' || _code[codeIndex] == '\t') {
                    continue;
                } else if (abortTerminators != null && abortTerminators.Contains(_code[codeIndex])) {
                    break;
                } else if (terminators == null || terminators.Contains(_code[codeIndex])) {
                    // hit a terminator replace the indentation with new indentation
                    MaybeReplaceText(codeIndex + 1, start, newWhiteSpace);
                    break;
                } else {
                    break;
                }
            }
            if (codeIndex == -1) {
                MaybeReplaceText(0, start, newWhiteSpace);
            }
        }

        /// <summary>
        /// Replaces the preceeding whitespace updating it to one string if we hit a newline, or another string
        /// if we don't.
        /// </summary>
        private bool ReplacePreceedingWhiteSpaceMaybeMultiline(int start, char replaceOn = '\0') {
            int codeIndex;
            for (codeIndex = start - 1; codeIndex >= 0; codeIndex--) {
                if (_code[codeIndex] == ' ' || _code[codeIndex] == '\t') {
                    continue;
                } else if (_code[codeIndex] == '\r' || _code[codeIndex] == '\n') {
                    // hit a newline, replace the indentation with new indentation
                    MaybeReplaceText(codeIndex + 1, start, GetIndentation());
                    return true;
                } else if (replaceOn == 0 || _code[codeIndex] == replaceOn) {
                    MaybeReplaceText(codeIndex + 1, start, " ");
                    break;
                } else {
                    break;
                }
            }
            if (codeIndex == -1) {
                MaybeReplaceText(0, start, GetIndentation());
            }
            return false;
        }

        /// <summary>
        /// Generates an edit to replace the text in the provided range with the
        /// new text if the text has changed.
        /// </summary>
        private void MaybeReplaceText(int start, int end, string newText) {
            string indentation = newText ?? GetIndentation();
            int existingWsLength = end - start;

            if (existingWsLength != indentation.Length ||
                String.Compare(_code, start, indentation, 0, indentation.Length) != 0) {
                Debug.Assert(_edits.Count == 0 || _edits[_edits.Count - 1].Start <= start, "edits should be provided in order");

                _edits.Add(new Edit(start, existingWsLength, indentation));
            }
        }

        private void EnsureSpacesAroundParenthesisedExpression(Expression expr) {
            ReplacePreceedingWhiteSpace(
                expr.StartIndex,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : "",
                _openParen
            );
            expr.Walk(this);
            ReplaceFollowingWhiteSpace(
                expr.EndIndex,
                _options.SpaceAfterOpeningAndBeforeClosingNonEmptyParenthesis ? " " : ""
            );
        }

        #endregion
    }
}