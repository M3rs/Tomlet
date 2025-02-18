﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tomlet.Exceptions;
using Tomlet.Models;

namespace Tomlet
{
    public class TomlParser
    {
        private static readonly char[] TrueChars = {'t', 'r', 'u', 'e'};
        private static readonly char[] FalseChars = {'f', 'a', 'l', 's', 'e'};

        private int _lineNumber = 1;

        private string? _lastTableArrayName;
        private TomlTable? _currentTable;

        public static TomlDocument ParseFile(string filePath)
        {
            var fileContent = File.ReadAllText(filePath);
            TomlParser parser = new();
            return parser.Parse(fileContent);
        }

        public TomlDocument Parse(string input)
        {
            try
            {
                var document = new TomlDocument();
                using var reader = new StringReader(input);

                while (reader.TryPeek(out _))
                {
                    //We have more to read.
                    _lineNumber += reader.SkipAnyCommentNewlineWhitespaceEtc();

                    if (!reader.TryPeek(out var nextChar))
                        break;

                    if (nextChar == '[')
                    {
                        reader.Read(); //Consume the [

                        //Table or table-array?
                        if (!reader.TryPeek(out var potentialSecondBracket))
                            throw new TomlEOFException(_lineNumber);

                        if (potentialSecondBracket != '[')
                            ReadTableStatement(reader, document);
                        else
                            ReadTableArrayStatement(reader, document);

                        continue; //Restart loop.
                    }

                    //Read a key-value pair
                    ReadKeyValuePair(reader, out var key, out var value);

                    if (_currentTable != null)
                        //Insert into current table
                        _currentTable.ParserPutValue(key, value, _lineNumber);
                    else
                        //Insert into the document
                        document.ParserPutValue(key, value, _lineNumber);

                    //Read up until the end of the line, ignoring any comments or whitespace
                    reader.SkipWhitespace();
                    reader.SkipAnyComment();

                    //Ensure we have a newline
                    reader.SkipPotentialCR();
                    if (!reader.ExpectAndConsume('\n') && reader.TryPeek(out var shouldHaveBeenLF))
                        //Not EOF and found a non-newline char
                        throw new TomlMissingNewlineException(_lineNumber, (char) shouldHaveBeenLF);

                    _lineNumber++; //We've consumed a newline, move to the next line number.
                }

                return document;
            }
            catch (Exception e) when (!(e is TomlException))
            {
                throw new TomlInternalException(_lineNumber, e);
            }
        }

        private void ReadKeyValuePair(StringReader reader, out string key, out TomlValue value)
        {
            //Read the key
            key = ReadKey(reader);

            //Consume the equals sign, potentially with whitespace either side.
            reader.SkipWhitespace();
            if (!reader.ExpectAndConsume('='))
            {
                if (reader.TryPeek(out var shouldHaveBeenEquals))
                    throw new TomlMissingEqualsException(_lineNumber, (char) shouldHaveBeenEquals);

                throw new TomlEOFException(_lineNumber);
            }

            reader.SkipWhitespace();

            //Read the value
            value = ReadValue(reader);
        }

        private string ReadKey(StringReader reader)
        {
            reader.SkipWhitespace();

            if (!reader.TryPeek(out var nextChar))
                return "";

            if (nextChar.IsEquals())
                throw new NoTomlKeyException(_lineNumber);

            //Read a key
            reader.SkipWhitespace();

            string key;
            if (nextChar.IsDoubleQuote())
            {
                reader.Read(); //Consume opening quote
                //Read double-quoted key
                key = '"' + reader.ReadWhile(keyChar => !keyChar.IsNewline() && !keyChar.IsDoubleQuote()) + '"';
                if (!reader.ExpectAndConsume('"'))
                    throw new UnterminatedTomlKeyException(_lineNumber);
            }
            else if (nextChar.IsSingleQuote())
            {
                reader.Read(); //Consume opening quote.

                //Read single-quoted key
                key = "'" + reader.ReadWhile(keyChar => !keyChar.IsNewline() && !keyChar.IsSingleQuote()) + "'";
                if (!reader.ExpectAndConsume('\''))
                    throw new UnterminatedTomlKeyException(_lineNumber);
            }
            else
                //Read unquoted key
                key = reader.ReadWhile(keyChar => !keyChar.IsEquals() && !keyChar.IsHashSign());

            key = key.Replace("\\n", "\n")
                .Replace("\\t", "\t");

            return key;
        }

        private TomlValue ReadValue(StringReader reader)
        {
            if (!reader.TryPeek(out var startOfValue))
                throw new TomlEOFException(_lineNumber);

            TomlValue value;
            switch (startOfValue)
            {
                case '[':
                    //Array
                    value = ReadArray(reader);
                    break;
                case '{':
                    //Inline table
                    value = ReadInlineTable(reader);
                    break;
                case '"':
                case '\'':
                    //Basic or Literal String, maybe multiline
                    var startQuote = reader.Read();
                    var maybeSecondQuote = reader.Peek();
                    if (maybeSecondQuote != startQuote)
                        //Second char is not first, this is a single-line string.
                        value = startQuote.IsSingleQuote() ? ReadSingleLineLiteralString(reader) : ReadSingleLineBasicString(reader);
                    else
                    {
                        reader.Read(); //Consume second char

                        //Check the third char. If it's another quote, we have a multiline string. If it's whitespace, a newline, or a #, we have an empty string.
                        //Anything else is an error.
                        var maybeThirdQuote = reader.Peek();
                        if (maybeThirdQuote == startQuote)
                        {
                            reader.Read(); //Consume the third opening quote, for simplicity's sake.
                            value = startQuote.IsSingleQuote() ? ReadMultiLineLiteralString(reader) : ReadMultiLineBasicString(reader);
                        }
                        else if (maybeThirdQuote.IsWhitespace() || maybeThirdQuote.IsNewline() || maybeThirdQuote.IsHashSign() || maybeThirdQuote == -1)
                        {
                            value = TomlString.EMPTY;
                        }
                        else
                        {
                            throw new TomlStringException(_lineNumber);
                        }
                    }

                    break;
                case '+':
                case '-':
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case 'i':
                case 'n':
                    //I kind of hate that but it's probably fast.
                    //Number. Maybe floating-point.
                    //i and n indicate special floating point values (inf and nan).

                    //Read a string, stopping if we hit an equals, whitespace, newline, or comment.
                    var stringValue = reader.ReadWhile(valueChar => !valueChar.IsEquals() && !valueChar.IsNewline() && !valueChar.IsHashSign() && !valueChar.IsComma() && !valueChar.IsEndOfArrayChar() && !valueChar.IsEndOfInlineObjectChar()).ToLowerInvariant().Trim();

                    if (stringValue.Contains(':') || stringValue.Contains('t') || stringValue.Contains(' ') || stringValue.Contains('z'))
                        value = TomlDateTimeUtils.ParseDateString(stringValue, _lineNumber) ?? throw new InvalidTomlDateTimeException(_lineNumber, stringValue);
                    else if (stringValue.Contains('.') || stringValue.Contains('e') || stringValue.Contains('n') || stringValue.Contains('i'))
                        //Try parse as a double, then fall back to a date/time.
                        value = TomlDouble.Parse(stringValue) ?? TomlDateTimeUtils.ParseDateString(stringValue, _lineNumber) ?? throw new InvalidTomlNumberException(_lineNumber, stringValue);
                    else
                        //Try parse as a long, then fall back to a date/time.
                        value = TomlLong.Parse(stringValue) ?? TomlDateTimeUtils.ParseDateString(stringValue, _lineNumber) ?? throw new InvalidTomlNumberException(_lineNumber, stringValue);

                    break;
                case 't':
                {
                    //Either "true" or an error
                    var charsRead = reader.ReadChars(4);

                    if (!TrueChars.SequenceEqual(charsRead))
                        throw new TomlInvalidValueException(_lineNumber, (char) startOfValue);

                    value = TomlBoolean.TRUE;
                    break;
                }
                case 'f':
                {
                    //Either "false" or an error
                    var charsRead = reader.ReadChars(5);

                    if (!FalseChars.SequenceEqual(charsRead))
                        throw new TomlInvalidValueException(_lineNumber, (char) startOfValue);

                    value = TomlBoolean.FALSE;
                    break;
                }
                default:
                    throw new TomlInvalidValueException(_lineNumber, (char) startOfValue);
            }

            return value;
        }

        private TomlValue ReadSingleLineBasicString(StringReader reader)
        {
            //No simple read here, we have to accomodate escaped double quotes.
            var content = new StringBuilder();

            var escapeMode = false;
            var fourDigitUnicodeMode = false;
            var eightDigitUnicodeMode = false;

            var unicodeStringBuilder = new StringBuilder();
            while (reader.TryPeek(out _))
            {
                var nextChar = reader.Read();

                if (nextChar == '"' && !escapeMode)
                    break;

                if (nextChar == '\\' && !escapeMode)
                {
                    escapeMode = true;
                    continue; //Don't append
                }

                if (escapeMode)
                {
                    escapeMode = false;
                    var toAppend = HandleEscapedChar(nextChar, out fourDigitUnicodeMode, out eightDigitUnicodeMode);

                    if (toAppend.HasValue)
                        content.Append(toAppend.Value);
                    continue;
                }

                if (fourDigitUnicodeMode || eightDigitUnicodeMode)
                {
                    //Handle \u1234 and \U12345678
                    unicodeStringBuilder.Append((char) nextChar);

                    if (fourDigitUnicodeMode && unicodeStringBuilder.Length == 4 || eightDigitUnicodeMode && unicodeStringBuilder.Length == 8)
                    {
                        var unicodeString = unicodeStringBuilder.ToString();

                        content.Append(DecipherUnicodeEscapeSequence(unicodeString, fourDigitUnicodeMode));

                        fourDigitUnicodeMode = false;
                        eightDigitUnicodeMode = false;
                        unicodeStringBuilder = new StringBuilder();
                    }

                    continue;
                }

                if (nextChar.IsNewline())
                    throw new UnterminatedTomlStringException(_lineNumber);

                content.Append((char) nextChar);
            }

            return new TomlString(content.ToString());
        }

        private char[] DecipherUnicodeEscapeSequence(string unicodeString, bool fourDigitMode)
        {
            char[] toAppend;
            if (unicodeString.Any(c => !c.IsHexDigit()))
                throw new InvalidTomlEscapeException(_lineNumber, $"\\{(fourDigitMode ? 'u' : 'U')}{unicodeString}");

            if (fourDigitMode)
            {
                //16-bit char
                var decodedChar = short.Parse(unicodeString, NumberStyles.HexNumber);
                toAppend = new[] {(char) decodedChar};
            }
            else
            {
                //32-bit char
                var decodedChars = int.Parse(unicodeString, NumberStyles.HexNumber);
                var chars = Encoding.Unicode.GetChars(BitConverter.GetBytes(decodedChars));
                toAppend = chars;
            }

            return toAppend;
        }

        private char? HandleEscapedChar(int escapedChar, out bool fourDigitUnicodeMode, out bool eightDigitUnicodeMode, bool allowNewline = false)
        {
            eightDigitUnicodeMode = false;
            fourDigitUnicodeMode = false;

            char toAppend;
            switch (escapedChar)
            {
                case 'b':
                    toAppend = '\b';
                    break;
                case 't':
                    toAppend = '\t';
                    break;
                case 'n':
                    toAppend = '\n';
                    break;
                case 'f':
                    toAppend = '\f';
                    break;
                case 'r':
                    toAppend = '\r';
                    break;
                case '"':
                    toAppend = '"';
                    break;
                case '\\':
                    toAppend = '\\';
                    break;
                case 'u':
                    fourDigitUnicodeMode = true;
                    return null;
                case 'U':
                    eightDigitUnicodeMode = true;
                    return null;
                default:
                    if (allowNewline && escapedChar.IsNewline())
                        return null;
                    throw new InvalidTomlEscapeException(_lineNumber, $"\\{escapedChar}");
            }

            return toAppend;
        }

        private TomlValue ReadSingleLineLiteralString(StringReader reader)
        {
            //Literally (hah) just read until a single-quote
            var stringContent = reader.ReadWhile(valueChar => !valueChar.IsSingleQuote() && !valueChar.IsNewline());

            if (!reader.TryPeek(out var terminatingChar))
                //Unexpected EOF
                throw new TomlEOFException(_lineNumber);

            if (!terminatingChar.IsSingleQuote())
                throw new UnterminatedTomlStringException(_lineNumber);

            reader.Read(); //Consume terminating quote.

            return new TomlString(stringContent);
        }

        private TomlValue ReadMultiLineLiteralString(StringReader reader)
        {
            var content = new StringBuilder();
            //Ignore any first-line newlines
            _lineNumber += reader.SkipAnyNewline();
            while (reader.TryPeek(out _))
            {
                var nextChar = reader.Read();

                if (!nextChar.IsSingleQuote())
                {
                    content.Append((char) nextChar);

                    if (nextChar == '\n')
                        _lineNumber++; //We've wrapped to a new line.

                    continue;
                }

                //We have a single quote.
                //Is it alone? if so, just continue.
                if (!reader.TryPeek(out var potentialSecondQuote) || !potentialSecondQuote.IsSingleQuote())
                {
                    content.Append('\'');
                    continue;
                }

                //We have two quotes in a row. Consume the second one
                reader.Read();

                //Do we have three?
                if (!reader.TryPeek(out var potentialThirdQuote) || !potentialThirdQuote.IsSingleQuote())
                {
                    content.Append('\'');
                    content.Append('\'');
                    continue;
                }

                //Ok we have at least three quotes. Consume the third.
                reader.Read();

                if (!reader.TryPeek(out var afterThirdQuote) || !afterThirdQuote.IsSingleQuote())
                    //And ONLY three quotes. End of literal.
                    break;

                //We're at 4 single quotes back-to-back at this point, and the max is 5. I'm just going to do this without a loop because it's probably actually less code.
                //Consume the fourth.
                reader.Read();
                //And we have to append one single quote to our string.
                content.Append('\'');

                //Check for a 5th.
                if (!reader.TryPeek(out var potentialFifthQuote) || !potentialFifthQuote.IsSingleQuote())
                    //Four in total, so we bail out here.
                    break;

                //We have a 5th. Consume it.
                reader.Read();
                //And append to output
                content.Append('\'');

                //Check for sixth
                if (!reader.TryPeek(out var potentialSixthQuote) || !potentialSixthQuote.IsSingleQuote())
                    //Five in total, so we bail out here.
                    break;

                //We have a sixth. This is a syntax error.
                throw new TripleQuoteInTomlMultilineLiteralException(_lineNumber);
            }

            return new TomlString(content.ToString());
        }

        private TomlValue ReadMultiLineBasicString(StringReader reader)
        {
            var content = new StringBuilder();

            var escapeMode = false;
            var fourDigitUnicodeMode = false;
            var eightDigitUnicodeMode = false;

            var unicodeStringBuilder = new StringBuilder();

            //Leading newlines are ignored
            _lineNumber += reader.SkipAnyNewline();

            while (reader.TryPeek(out _))
            {
                var nextChar = reader.Read();

                if (nextChar == '\\' && !escapeMode)
                {
                    escapeMode = true;
                    continue; //Don't append
                }

                if (escapeMode)
                {
                    escapeMode = false;
                    var toAppend = HandleEscapedChar(nextChar, out fourDigitUnicodeMode, out eightDigitUnicodeMode, true);

                    if (toAppend.HasValue)
                        content.Append(toAppend.Value);
                    else if (nextChar.IsNewline())
                    {
                        //Ensure we've fully consumed the newline
                        if (nextChar == '\r' && !reader.ExpectAndConsume('\n'))
                            throw new Exception($"Found a CR without an LF on line {_lineNumber}");

                        //Increment line number
                        _lineNumber++;

                        //Escaped newline indicates we skip this newline and any whitespace at the start of the next line
                        reader.SkipAnyNewlineOrWhitespace();
                    }

                    continue;
                }

                if (fourDigitUnicodeMode || eightDigitUnicodeMode)
                {
                    //Handle \u1234 and \U12345678
                    unicodeStringBuilder.Append((char) nextChar);

                    if (fourDigitUnicodeMode && unicodeStringBuilder.Length == 4 || eightDigitUnicodeMode && unicodeStringBuilder.Length == 8)
                    {
                        var unicodeString = unicodeStringBuilder.ToString();

                        content.Append(DecipherUnicodeEscapeSequence(unicodeString, fourDigitUnicodeMode));

                        fourDigitUnicodeMode = false;
                        eightDigitUnicodeMode = false;
                        unicodeStringBuilder = new StringBuilder();
                    }

                    continue;
                }

                if (!nextChar.IsDoubleQuote())
                {
                    if (nextChar == '\n')
                        _lineNumber++;

                    content.Append((char) nextChar);
                    continue;
                }

                //Like above, check for up to 6 quotes.

                //We have a double quote.
                //Is it alone? if so, just continue.
                if (!reader.TryPeek(out var potentialSecondQuote) || !potentialSecondQuote.IsDoubleQuote())
                {
                    content.Append('"');
                    continue;
                }

                //We have two quotes in a row. Consume the second one
                reader.Read();

                //Do we have three?
                if (!reader.TryPeek(out var potentialThirdQuote) || !potentialThirdQuote.IsDoubleQuote())
                {
                    content.Append('"');
                    content.Append('"');
                    continue;
                }

                //Ok we have at least three quotes. Consume the third.
                reader.Read();

                if (!reader.TryPeek(out var afterThirdQuote) || !afterThirdQuote.IsDoubleQuote())
                    //And ONLY three quotes. End of literal.
                    break;

                //Like above, just going to bruteforce this out instead of writing a loop.
                //Consume the fourth.
                reader.Read();
                //And we have to append one double quote to our string.
                content.Append('"');

                //Check for a 5th.
                if (!reader.TryPeek(out var potentialFifthQuote) || !potentialFifthQuote.IsDoubleQuote())
                    //Four in total, so we bail out here.
                    break;

                //We have a 5th. Consume it.
                reader.Read();
                //And append to output
                content.Append('"');

                //Check for sixth
                if (!reader.TryPeek(out var potentialSixthQuote) || !potentialSixthQuote.IsDoubleQuote())
                    //Five in total, so we bail out here.
                    break;

                //We have a sixth. This is a syntax error.
                throw new TripleQuoteInTomlMultilineSimpleStringException(_lineNumber);
            }

            return new TomlString(content.ToString());
        }

        private TomlArray ReadArray(StringReader reader)
        {
            //Consume the opening bracket
            if (!reader.ExpectAndConsume('['))
                throw new ArgumentException("Internal Tomlet Bug: ReadArray called and first char is not a [");

            //Move to the first value
            _lineNumber += reader.SkipAnyCommentNewlineWhitespaceEtc();

            var result = new TomlArray();

            while (reader.TryPeek(out _))
            {
                //Skip any empty lines
                _lineNumber += reader.SkipAnyCommentNewlineWhitespaceEtc();

                if (!reader.TryPeek(out var nextChar))
                    throw new TomlEOFException(_lineNumber);

                //Check for end of array here (helps with trailing commas, which are legal)
                if (nextChar.IsEndOfArrayChar())
                    break;

                //Read a value
                result.ArrayValues.Add(ReadValue(reader));

                //Skip any whitespace or newlines, NOT comments - that would be a syntax error
                _lineNumber += reader.SkipAnyNewlineOrWhitespace();

                if (!reader.TryPeek(out var postValueChar))
                    throw new TomlEOFException(_lineNumber);

                if (postValueChar.IsEndOfArrayChar())
                    break; //end of array

                if (!postValueChar.IsComma())
                    throw new TomlArraySyntaxException(_lineNumber, (char) postValueChar);

                reader.ExpectAndConsume(','); //We've already verified we have one.
            }

            if (!reader.ExpectAndConsume(']'))
                throw new UnterminatedTomlArrayException(_lineNumber);

            return result;
        }

        private TomlTable ReadInlineTable(StringReader reader)
        {
            //Consume the opening brace
            if (!reader.ExpectAndConsume('{'))
                throw new ArgumentException("Internal Tomlet Bug: ReadInlineTable called and first char is not a {");

            //Move to the first key
            _lineNumber += reader.SkipAnyCommentNewlineWhitespaceEtc();

            var result = new TomlTable();

            while (reader.TryPeek(out _))
            {
                //Skip any whitespace. Do not skip comments or newlines, those aren't allowed. 
                reader.SkipWhitespace();

                if (!reader.TryPeek(out var nextChar))
                    throw new TomlEOFException(_lineNumber);

                //Note that this is only needed when we first enter the loop, in case of an empty inline table
                if (nextChar.IsEndOfInlineObjectChar())
                    break;

                //Newlines are not permitted
                if (nextChar.IsNewline())
                    throw new NewLineInTomlInlineTableException(_lineNumber);

                //Note that unlike in the above case, we do not check for the end of the value here. Trailing commas aren't permitted
                //and so all cases where the table ends should be handled at the end of this look
                try
                {
                    //Read a key-value pair
                    ReadKeyValuePair(reader, out var key, out var value);
                    //Insert into the table
                    result.ParserPutValue(key, value, _lineNumber);
                }
                catch (TomlException ex) when (ex is TomlMissingEqualsException || ex is NoTomlKeyException)
                {
                    //Wrap missing keys or equals signs in a parent exception.
                    throw new InvalidTomlInlineTableException(_lineNumber, ex);
                }

                if (!reader.TryPeek(out var postValueChar))
                    throw new TomlEOFException(_lineNumber);

                if (reader.ExpectAndConsume(','))
                    continue; //Comma, we have more.

                //Non-comma, consume any whitespace
                reader.SkipWhitespace();

                if (!reader.TryPeek(out postValueChar))
                    throw new TomlEOFException(_lineNumber);

                if (postValueChar.IsEndOfInlineObjectChar())
                    break; //end of table

                throw new TomlInlineTableSeparatorException(_lineNumber, (char) postValueChar);
            }

            if (!reader.ExpectAndConsume('}'))
                throw new UnterminatedTomlInlineObjectException(_lineNumber);

            result.Locked = true; //Defined inline, cannot be later modified
            return result;
        }

        private void ReadTableStatement(StringReader reader, TomlDocument document)
        {
            //Table name
            var currentTableKey = reader.ReadWhile(c => !c.IsEndOfArrayChar() && !c.IsNewline());

            var parent = (TomlTable) document;
            var relativeKey = currentTableKey;

            if (_lastTableArrayName != null && currentTableKey.StartsWith(_lastTableArrayName + "."))
            {
                parent = (TomlTable) document.GetArray(_lastTableArrayName).Last();
                relativeKey = relativeKey.Replace(_lastTableArrayName + ".", "");
            }

            try
            {
                if (parent.ContainsKey(relativeKey))
                {
                    try
                    {
                        // this is an intentional variable, resharper.

                        // ReSharper disable once UnusedVariable
                        var table = (TomlTable) parent.GetValue(relativeKey);

                        //The cast succeeded - we are redefining an existing table
                        throw new TomlTableRedefinitionException(_lineNumber, currentTableKey);
                    }
                    catch (InvalidCastException)
                    {
                        //The cast failed, we are re-defining a non-table.
                        throw new TomlKeyRedefinitionException(_lineNumber, currentTableKey);
                    }
                }
            }
            catch (TomlContainsDottedKeyNonTableException e)
            {
                //Re-throw with correct line number and exception type.
                //To be clear - here we're re-defining a NON-TABLE key as a table, so this is a key redefinition exception
                //while the one above is a TableRedefinition exception because it's re-defining a key which is already a table.
                throw new TomlKeyRedefinitionException(_lineNumber, e._key, e);
            }

            if (!reader.TryPeek(out _))
                throw new TomlEOFException(_lineNumber);

            if (!reader.ExpectAndConsume(']'))
                throw new UnterminatedTomlTableNameException(_lineNumber);

            reader.SkipWhitespace();
            reader.SkipAnyComment();
            reader.SkipPotentialCR();

            if (!reader.TryPeek(out var shouldBeNewline))
                throw new TomlEOFException(_lineNumber);

            if (!shouldBeNewline.IsNewline())
                throw new TomlMissingNewlineException(_lineNumber, (char) shouldBeNewline);

            _currentTable = new TomlTable();
            _lastTableArrayName = null;
            parent.ParserPutValue(relativeKey, _currentTable, _lineNumber);
        }

        private void ReadTableArrayStatement(StringReader reader, TomlDocument document)
        {
            //Consume the (second) opening bracket
            if (!reader.ExpectAndConsume('['))
                throw new ArgumentException("Internal Tomlet Bug: ReadTableArrayStatement called and first char is not a [");

            //Array
            var arrayName = reader.ReadWhile(c => !c.IsEndOfArrayChar() && !c.IsNewline());

            if (!reader.ExpectAndConsume(']') || !reader.ExpectAndConsume(']'))
                throw new UnterminatedTomlTableArrayException(_lineNumber);

            TomlTable parentTable;
            if (_lastTableArrayName != null && arrayName.StartsWith(_lastTableArrayName + "."))
            {
                //nested array of tables directly relative to parent - we can cheat

                //Save parent table
                parentTable = _currentTable!;

                //Work out relative key
                var relativeKey = arrayName.Replace(_lastTableArrayName + ".", "");

                //Make new array and table
                var newArray = new TomlArray();
                _currentTable = new TomlTable();
                newArray.ArrayValues.Add(_currentTable);

                //Insert into parent table
                parentTable!.ParserPutValue(relativeKey, newArray, _lineNumber);

                //Save variables
                _lastTableArrayName = arrayName;
                return;
            }

            if (TomlKeyUtils.IsSimpleKey(arrayName))
            {
                //Not present - create and populate with one table.
                _currentTable = new TomlTable();

                TomlArray tableArray;

                //Simple key so looking up via document.ContainsKey is fine.
                if (!document.ContainsKey(arrayName))
                    //make a new one if it doesn't exist
                    tableArray = new TomlArray {IsTableArray = true};
                else if (document.Entries.TryGetValue(arrayName, out var hopefullyArray) && hopefullyArray is TomlArray arr)
                    //already exists, use it
                    tableArray = arr;
                else
                    throw new TomlTableArrayAlreadyExistsAsNonArrayException(_lineNumber, arrayName);

                if (!tableArray.IsTableArray)
                    throw new TomlNonTableArrayUsedAsTableArrayException(_lineNumber, arrayName);

                tableArray.ArrayValues.Add(_currentTable);

                if (!document.ContainsKey(arrayName))
                    //Insert into the document
                    document.ParserPutValue(arrayName, tableArray, _lineNumber);

                //Save variables
                _lastTableArrayName = arrayName;
                return;
            }

            //Need to add to a complex-keyed table array, so may be behind one or more table arrays.

            parentTable = document;
            var components = TomlKeyUtils.GetKeyComponents(arrayName).ToList();

            //Don't check last component
            for (var index = 0; index < components.Count - 1; index++)
            {
                var pathComponent = components[index];

                if (!parentTable.ContainsKey(pathComponent))
                    throw new MissingIntermediateInTomlTableArraySpecException(_lineNumber, pathComponent);

                var value = parentTable.GetValue(pathComponent);

                if (value is TomlArray intermediateArray)
                {
                    if (intermediateArray.Last() is TomlTable table)
                        parentTable = table;
                    else
                        throw new TomlTableArrayIntermediateNonTableException(_lineNumber, arrayName);
                }
                else if (value is TomlTable table)
                    parentTable = table;
                else
                    throw new TomlKeyRedefinitionException(_lineNumber, pathComponent);
            }

            var lastComponent = components.Last();
            if (parentTable.ContainsKey(lastComponent))
            {
                if (!(parentTable.GetValue(lastComponent) is TomlArray array))
                    throw new TomlTableArrayAlreadyExistsAsNonArrayException(_lineNumber, lastComponent);

                if (!array.IsTableArray)
                    throw new TomlNonTableArrayUsedAsTableArrayException(_lineNumber, arrayName);

                _currentTable = new TomlTable();
                array.ArrayValues.Add(_currentTable);

                _lastTableArrayName = arrayName;
            }
            else
            {
                var array = new TomlArray {IsTableArray = true};
                _currentTable = new TomlTable();
                array.ArrayValues.Add(_currentTable);

                parentTable.PutValue(lastComponent, array);

                _lastTableArrayName = arrayName;
            }
        }
    }
}