#pragma once

/* DefaultLut.h
*
*  MIT License
*
*  Copyright (c) 2020-2026 awawa-dev
*
*  Project homesite: https://github.com/awawa-dev/HyperHDR
*
*  Permission is hereby granted, free of charge, to any person obtaining a copy
*  of this software and associated documentation files (the "Software"), to deal
*  in the Software without restriction, including without limitation the rights
*  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*  copies of the Software, and to permit persons to whom the Software is
*  furnished to do so, subject to the following conditions:
*
*  The above copyright notice and this permission notice shall be included in all
*  copies or substantial portions of the Software.

*  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*  SOFTWARE.
*/
#ifndef PCH_ENABLED
	#include <QString>
#endif

#include <cstdint>

// The default HDR to SDR tone mapping table of the system grabber (the content of 'lut_lin_tables.3d' when the user
// has not installed an own one). It maps HDR10 (PQ, BT.2020) RGB captured as BGRA to SDR sRGB.
namespace DefaultLut
{
	// fills LutLoader::TABLE_SIZE bytes, indexed with LUT_INDEX(r, g, b)
	void fillTable(uint8_t* table);

	// writes the table to <directory>/lut_lin_tables.3d, returns an error message or an empty string
	QString createFile(const QString& directory);

	// createFile() on the global thread pool, only once per process
	void createFileInBackground(const QString& directory);
}
