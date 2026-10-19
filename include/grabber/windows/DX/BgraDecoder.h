#pragma once

/* BgraDecoder.h
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
	#include <cstdint>
#endif

#include <image/Image.h>
#include <image/ColorRgb.h>

// Scales down (takes every 'division' pixel) and converts a BGRA frame to RGB, optionally through the HDR tone mapping
// table (lutBuffer: LutLoader::TABLE_SIZE bytes, indexed with LUT_INDEX; nullptr for a plain copy). A lineSize of 0
// means rows of actualWidth * 4 bytes. Used by the system grabber.
void decodeBGRA(Image<ColorRgb>& image, int targetSizeX, int targetSizeY,
	int startX, int startY,
	const uint8_t* source, int actualWidth, int actualHeight,
	int division, const uint8_t* lutBuffer, int lineSize);
