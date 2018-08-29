////------------------------------------------------------------------------------------------
//
//    Merlinia Project Yacks
//     Copyright © Merlinia 2018, All Rights Reserved. 
//     Licensed under the Apache License, Version 2.0.
//     (Just to be compatible with the Microsoft Roslyn license.)
//
////------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;


namespace FuzzyPatch
{
	/// <summary>
	/// Program to process the "diff" files created by the Generate Diff program, applying them to a 
	/// new revision of the Microsoft Roslyn compiler, and thus transferring the "Project Yacks" 
	/// modifications from one Roslyn revision to another.
	/// 
	/// Input to this program should be two arguments like this:
	/// 
	///   "E:\Roslyn\32645" "E:\Roslyn\33284"
	/// 
	/// The first folder must contain a sub-folder "Diff", containing files produced by the Generate 
	/// Diff program.
	/// 
	/// The second folder must contain a sub-folder "Current", containing newly-downloaded Roslyn 
	/// files from the GitHub repository https://github.com/dotnet/roslyn .
	/// 
	/// (The numbers 32645 and 33284 in the above example are Roslyn revision numbers, or, more 
	/// accurately, the number of commits that were applied to the GitHub repository at the time when 
	/// the downloads were done.)
	/// 
	/// This program will attempt to apply the .diff files from the prior revision to the new 
	/// revision, thus transferring the "Project Yacks" modifications from one Roslyn revision to 
	/// another. The processing is referred to as a "fuzzy patch" because it attempts to take into 
	/// consideration that the Roslyn files may have been updated. However, the processing is not all 
	/// that sophisticated, and is based on the expectation that if changes have been made, then they 
	/// are more likely to consist of added lines of code rather than lines of code removed or 
	/// rearranged.
	/// 
	/// In situations where the "fuzzy patch" processing can not figure out where to apply the .diff 
	/// updates with a high degree of certainty it displays an error message, and a programmer will 
	/// have to examine the situation and copy the modifications manually, perhaps after modifying 
	/// the modifications.
	/// 
	/// It is assumed that the Roslyn files use Unix-style end-of-line, i.e., LF, not CRLF, and that 
	/// the text is encoded UTF-8 with BOM.
	/// 
	/// This program also copies the C# source files that have been added to Roslyn and have been 
	/// noted as such by the Generate Diff program. This processing is very simple, and should always 
	/// work.
	/// </summary>
	public static class Program
	{
		/// <summary>
		/// Nested class describing a "hunk" in the .diff file. See here for documentation:
		/// http://www.gnu.org/software/diffutils/manual/html_node/Detailed-Unified.html
		/// </summary>
		private class DiffHunk
		{
			// All lines in the .diff file, null = end of .diff file
			public string[] DiffLines { get; }

			// Zero-based index for first line (if any) inside the .diff hunk
			public int DiffLineIndex { get; }

			// Number of lines inside the .diff hunk, may be zero
			public int HunkLines { get; }

			// One-based line number for start location in old Roslyn source line
			public int FromFileLineNumber { get; }

			// Constructor
			public DiffHunk(string[] diffLines, int diffLineIndex, int hunkLines,
							int fromFileLineNumber)
			{
				DiffLines = diffLines;
				DiffLineIndex = diffLineIndex;
				HunkLines = hunkLines;
				FromFileLineNumber = fromFileLineNumber;
			}
		}


		/// <summary>
		/// Nested class that encapsulates some information to make it easier to process applying the 
		/// .diff file to the new Roslyn source file.
		/// </summary>
		private class NewFile
		{
			// Text lines in the new file
			public List<string> NewFileLines { get; }

			public int LinesRemoved { get; private set; }
			public int LinesAdded { get; private set; }

			// The "displacementFactor" is an attempt to provide an indication of how different the new 
			//  revision is from the old one.
			public int DisplacementFactor { get; private set; }


			// Index of last recognized or modified line. Trying to find a new hunk location must not 
			//  go farther back in the file than this point.
			private int _fenceIndex;


			// Constructor
			public NewFile(IEnumerable<string> newFileLines)
			{
				NewFileLines = new List<string>(newFileLines);
			}


			/// <summary>
			/// Method to find the location where the "hunk" from the .diff file should be applied to 
			/// the new file.
			/// </summary>
			/// <returns>zero-based index, or -1 for not found</returns>
			public int FindHunkLocation(DiffHunk diffHunk)
			{
				// As a "first guess", assume specified start line (-1 to make zero-based) is correct, 
				//  adjusted for the application of previous hunks
				int firstGuessIndex = diffHunk.FromFileLineNumber - 1 - LinesRemoved + LinesAdded;
				bool hunkFound = false;

				// Search forward to try to find the hunk, on the assumption it is more likely that 
				//  lines have been added to the new file than that lines have been removed
				int testIndex = firstGuessIndex;
				for (; testIndex < NewFileLines.Count; testIndex++)
				{
					if (TestHunkForMatch(diffHunk, testIndex))
					{
						hunkFound = true;
						break;
					}
				}

				// If not found via forward search try searching backwards, but only to the "fence 
				//  index", so the search does not get back into lines that have already been updated
				if (!hunkFound)
				{
					testIndex = firstGuessIndex - 1;
					for (; testIndex > _fenceIndex; testIndex--)
					{
						if (TestHunkForMatch(diffHunk, testIndex))
						{
							hunkFound = true;
							break;
						}
					}
				}

				// Indicate result of the search, and adjust the "displacement factor"
				if (!hunkFound)
					return -1;

				DisplacementFactor += Math.Abs(firstGuessIndex - testIndex);
				return testIndex;
			}


			/// <summary>
			/// Method to apply a .diff file "hunk" to the new file. At this point everything has been 
			/// checked and there should not be any possible error situations.
			/// </summary>
			public void ApplyHunk(DiffHunk diffHunk, int newFileIndex)
			{
				for (int i = diffHunk.DiffLineIndex;
					 i < diffHunk.DiffLineIndex + diffHunk.HunkLines; i++)
				{
					string s = diffHunk.DiffLines[i];
					if (s.StartsWith(" ", StringComparison.Ordinal))
					{
						if (NewFileLines[newFileIndex] != s.Substring(1))
							throw new InvalidOperationException("Programming error.");
						newFileIndex++;
					}
					else if (s.StartsWith("-", StringComparison.Ordinal))
					{
						if (NewFileLines[newFileIndex] != s.Substring(1))
							throw new InvalidOperationException("Programming error.");
						NewFileLines.RemoveAt(newFileIndex);
						LinesRemoved++;
					}
					else if (s.StartsWith("+", StringComparison.Ordinal))
					{
						NewFileLines.Insert(newFileIndex, s.Substring(1));
						newFileIndex++;
						LinesAdded++;
					}
				}

				_fenceIndex = newFileIndex - 1;
			}


			/// <summary>
			/// Method to compare the lines in a .diff hunk with some lines in the new file to see if 
			/// they match. Added lines are ignored, but unchanged and removed lines must match 
			/// exactly, and there must be at least one unchanged or removed line to ensure a match.
			/// </summary>
			private bool TestHunkForMatch(DiffHunk diffHunk, int testIndex)
			{
				bool toReturn = false;  // In case no unchanged or removed lines
				for (int i = diffHunk.DiffLineIndex;
					 i < diffHunk.DiffLineIndex + diffHunk.HunkLines; i++)
				{
					string s = diffHunk.DiffLines[i];
					if (s.StartsWith("+", StringComparison.Ordinal))
						continue;
					if (testIndex >= NewFileLines.Count ||
						NewFileLines[testIndex] != s.Substring(1))
						return false;
					toReturn = true;
					testIndex++;
				}

				return toReturn;
			}
		}


		// Strange line that GNU diff.exe produces for input that it doesn't like
		private const string CNoNewlineAtEndOfFile = @"\ No newline at end of file";


		private static string _roslynPathOldDiff;
		private static string _roslynPathNewCurrent;


		/// <summary>
		/// Program entry point. Two arguments are needed - see above.
		/// </summary>
		public static void Main(string[] args)
		{
			// Check the two required arguments
			if (args.Length != 2)
			{
				DisplayErrorOrInfo("Exactly two arguments are needed.");
				return;
			}
			string roslynPathOld = CheckDirectoryExists(args[0]);
			string roslynPathNew = CheckDirectoryExists(args[1]);
			if (roslynPathOld == null || roslynPathNew == null)
				return;

			// Check the two expected sub-folders exist
			_roslynPathOldDiff = GetAndCheckCombinedPath(roslynPathOld, "Diff");
			_roslynPathNewCurrent = GetAndCheckCombinedPath(roslynPathNew, "Current");
			if (_roslynPathOldDiff == null || _roslynPathNewCurrent == null)
				return;
			Console.WriteLine("roslynPathOld: " + roslynPathOld);
			Console.WriteLine("roslynPathNew: " + roslynPathNew);
			Console.WriteLine("_roslynPathOldDiff: " + _roslynPathOldDiff);
			Console.WriteLine("_roslynPathNewCurrent: " + _roslynPathNewCurrent);
			// Process all of the files in the "Diffs" sub-folder of the old revision
			WalkDirectoryTree(new DirectoryInfo(_roslynPathOldDiff));
			DisplayErrorOrInfo("End of Fuzzy Patch program.");
		}


		/// <summary>
		/// Recursive method to "walk a directory tree", performing the specialized processing needed 
		/// to apply .diff files to a new revision of Roslyn, and also to copy added files to the new 
		/// revision. This is somewhat based on code found here:
		/// https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/file-system/how-to-iterate-through-a-directory-tree
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
		private static void WalkDirectoryTree(DirectoryInfo directoryInfo)
		{
			// First, process all the files directly under this folder
			FileInfo[] filesArray;
			try
			{
				filesArray = directoryInfo.GetFiles();
			}
			// Catch all possible exceptions. This includes the possible problem that one of the files 
			//  requires permissions greater than the application provides.
			catch (Exception e)
			{
				// Display an error message and exit
				DisplayErrorOrInfo(e.Message);
				return;
			}

			foreach (FileInfo oldDiffFileInfo in filesArray)
			{
				string oldFileFullName = oldDiffFileInfo.FullName;
				string newFileFullName =
								 oldFileFullName.Replace(_roslynPathOldDiff, _roslynPathNewCurrent);

				// Test for .diff file. If not, just copy the file to the new Roslyn revision.
				if (oldDiffFileInfo.Extension.ToUpperInvariant() != ".DIFF")
				{
					File.Copy(oldFileFullName, newFileFullName, true);
					Console.WriteLine("Added file copied to new revision: " + newFileFullName);
					continue;
				}

				// Do "fuzzy patch" processing for .diff file, after first checking the corresponding 
				//  source file still exists in the new revision of Roslyn
				newFileFullName = newFileFullName.Remove(newFileFullName.Length - ".DIFF".Length);
				if (File.Exists(newFileFullName))
					DoFuzzyPatch(oldFileFullName, newFileFullName);
				else
					DisplayErrorOrInfo("File to be modified not found: " + newFileFullName);
			}

			// Now find all the sub-directories under this directory
			foreach (DirectoryInfo subDirectoryInfo in directoryInfo.GetDirectories())
			{
				// Recursive call for each sub-directory
				WalkDirectoryTree(subDirectoryInfo);
			}
		}


		/// <summary>
		/// Method to apply a .diff file to a new Roslyn source file. A small amount of "fuzziness" is 
		/// accepted to take into account that the new file may have been updated in a new revision. 
		/// But the lines noted as unchanged and removed must not have been changed by the revision.
		/// </summary>
		private static void DoFuzzyPatch(string diffFileFilename, string newFileFilename)
		{
			// Read the two files into storage as lines of text
			string[] diffLines = File.ReadAllLines(diffFileFilename);
			NewFile newFile = new NewFile(File.ReadAllLines(newFileFilename));

			// Check the "new" file has not already been updated once. (This assumes .cs files will 
			//  have C# comments and/or identifier names that include "Yacks", and .csproj files will 
			//  reference the YacksCore assembly.)
			if (newFile.NewFileLines.Exists((string x) => x.Contains("Yacks")))
			{
				Console.WriteLine("File has already been updated once: " + newFileFilename);
				return;
			}

			// Check first two lines in .diff file look like they should, "---" and "+++"
			if (diffLines.Length < 3 || diffLines[0].Substring(0, 3) != "---" ||
				diffLines[1].Substring(0, 3) != "+++")
			{
				DisplayErrorOrInfo("Corrupt .diff file, invalid prefix lines: " + diffFileFilename);
				return;
			}
			int diffIndex = 2;  // Current zero-based location in the .diff file

			// Loop to process the "hunks" in the .diff file
			while (true)
			{
				// Find and check the next "hunk" in the .diff file
				DiffHunk diffHunk = GetNextDiffHunk(diffFileFilename, diffLines, ref diffIndex);
				if (diffHunk == null)
					return;  // Error encountered
				if (diffHunk.DiffLines == null)
					break;  // No more hunks in .diff file

				// Try to find the location in the new file that matches this hunk
				int newFileIndex = newFile.FindHunkLocation(diffHunk);
				if (newFileIndex == -1)
				{
					DisplayErrorOrInfo("Unable to find location to apply .diff file 'hunk' at line " +
									   diffHunk.DiffLineIndex + " for file " + diffFileFilename);
					return;
				}

				// Apply the .diff file "hunk"
				newFile.ApplyHunk(diffHunk, newFileIndex);
			}

			// Processing was successful if we get to here. Write the result to the disk and display 
			//  some info on the console.
			WriteToDisk(newFileFilename, newFile.NewFileLines);
			Console.WriteLine("File in new revision updated: " + newFileFilename);
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
							  "  Lines removed = {0}, lines added = {1}, displacement = {2}.",
							  newFile.LinesRemoved, newFile.LinesAdded, newFile.DisplacementFactor));
		}


		/// <summary>
		/// Method to do preliminary processing to find the next "hunk" in the .diff file.
		/// </summary>
		/// <returns>DiffHunk object, or null if something wrong (error message displayed)</returns>
		private static DiffHunk GetNextDiffHunk(string diffFile, string[] diffLines,
												ref int diffIndex)
		{
			// Test for end of .diff file - no more hunks
			if (diffIndex >= diffLines.Length ||
				diffLines[diffIndex] == CNoNewlineAtEndOfFile)
				return new DiffHunk(null, 0, 0, 0);

			// Get and check the hunk header line. The only item actually used is the "from file line 
			//  number start".
			string s = diffLines[diffIndex].Trim();
			Console.WriteLine("s=" + s);
			if (!s.StartsWith("@@", StringComparison.Ordinal)) /*||
				!s.EndsWith(" @@", StringComparison.Ordinal))*/
			{
				BadHunkHeaderOrContent(diffFile, diffIndex);
				return null;
			}
			string[] sa = s.Substring(4).Split(new char[] { ',', ' ' });
			int fromFileLineNumber;
			if (!int.TryParse(sa[0], out fromFileLineNumber))
			{
				BadHunkHeaderOrContent(diffFile, diffIndex);
				return null;
			}

			// Scan the hunk lines (if any), they must all start with " ", "-" or "+"
			int diffLineIndex = ++diffIndex;
			for (; diffIndex < diffLines.Length; diffIndex++)
			{
				s = diffLines[diffIndex];
				if (s.StartsWith(" ", StringComparison.Ordinal) ||
					s.StartsWith("-", StringComparison.Ordinal) ||
					s.StartsWith("+", StringComparison.Ordinal))
					continue;
				if (s.StartsWith("@@", StringComparison.Ordinal) ||
					s == CNoNewlineAtEndOfFile)
					break;
				BadHunkHeaderOrContent(diffFile, diffIndex);
				return null;
			}

			// Preliminary scan indicates OK hunk
			return
			   new DiffHunk(diffLines, diffLineIndex, diffIndex - diffLineIndex, fromFileLineNumber);
		}


		/// <summary>
		/// Method to display an error message for a corrupt hunk header line.
		/// </summary>
		private static void BadHunkHeaderOrContent(string diffFile, int diffIndex, string reason = "idk")
		{
			//Console.WriteLine("Reason: " + readon);

			StackFrame callStack = new StackFrame(1, true);
			Console.WriteLine("Error, File: " + callStack.GetFileName() + ", Line: " + callStack.GetFileLineNumber());
			DisplayErrorOrInfo("Corrupt .diff file at line " + (diffIndex + 1) + ": " + diffFile);
		}


		/// <summary>
		/// Method to generate a sub-folder path and check that it exists.
		/// </summary>
		private static string GetAndCheckCombinedPath(string roslynPathBase, string diffOrCurrent)
		{
			return CheckDirectoryExists(Path.Combine(roslynPathBase, diffOrCurrent));
		}


		/// <summary>
		/// Method to check a directory exists, and display an error message if not.
		/// </summary>
		private static string CheckDirectoryExists(string roslynPath)
		{
			if (Directory.Exists(roslynPath))
				return roslynPath;

			DisplayErrorOrInfo("Directory does not exist: " + roslynPath);
			return null;
		}


		/// <summary>
		/// Method to write lines of text to the disk, specifying that the newline sequence is just 
		/// LF, not CRLF, and the encoding is UTF-8 with BOM.
		/// </summary>
		private static void WriteToDisk(string fileName, IEnumerable<string> linesOfText)
		{
			using (StreamWriter streamWriter =
									 new StreamWriter(fileName, false, new UTF8Encoding(true)))
			{
				streamWriter.NewLine = "\n";
				foreach (string oneLine in linesOfText)
					streamWriter.WriteLine(oneLine);
			}
		}


		/// <summary>
		/// Method to display an error or information message on the console window.
		/// </summary>
		private static void DisplayErrorOrInfo(string textString)
		{
			Console.WriteLine(textString);
			Console.ReadKey();
		}
	}
}