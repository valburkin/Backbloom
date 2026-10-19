/* SystemPerformanceCounters.cpp
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
	#include <QTextStream>
	#include <QFile>

	#include <iostream>
	#include <cstdlib>
	#include <cstdio>
	#include <vector>	
#endif

#include <utils/InternalClock.h>
#include <performance-counters/SystemPerformanceCounters.h>

#include <windows.h>
#include <pdh.h>
#include <pdhmsg.h>
#pragma comment(lib, "pdh.lib")

PDH_HQUERY cpuPerfQuery = nullptr;
PDH_HCOUNTER cpuPerfTotal = nullptr;

void SystemPerformanceCounters::init()
{
	try
	{
		if (PdhOpenQuery(NULL, NULL, &cpuPerfQuery) == ERROR_SUCCESS)
		{
#ifdef UNICODE
			PdhAddEnglishCounter(cpuPerfQuery, L"\\Processor(*)\\% Processor Time", NULL, &cpuPerfTotal);
#else
			PdhAddEnglishCounter(cpuPerfQuery, "\\Processor(*)\\% Processor Time", NULL, &cpuPerfTotal);
#endif
			PdhCollectQueryData(cpuPerfQuery);
		}
	}
	catch (...)
	{

	}
	isInitialized = true;
}

SystemPerformanceCounters::~SystemPerformanceCounters()
{
	if (!isInitialized)
		return;

	if (cpuPerfQuery != nullptr)
		PdhRemoveCounter(cpuPerfQuery);
	cpuPerfQuery = nullptr;
}

QString SystemPerformanceCounters::getCPU()
{
	try
	{
		QJsonObject jResult;
		QJsonArray jCores;

		if (!isInitialized)
		{
			init();
			return "";
		}

		DWORD size = 0, count = 0;

		if (cpuPerfQuery == nullptr || PdhCollectQueryData(cpuPerfQuery) != ERROR_SUCCESS)
			return "";

		if (PdhGetFormattedCounterArray(cpuPerfTotal, PDH_FMT_LONG, &size, &count, nullptr) != PDH_MORE_DATA)
			return "";

		QByteArray myByteArray(size, 0);
		PDH_FMT_COUNTERVALUE_ITEM* bufferData = (PDH_FMT_COUNTERVALUE_ITEM*)myByteArray.data();

		if (PdhGetFormattedCounterArray(cpuPerfTotal, PDH_FMT_LONG, &size, &count, bufferData) != ERROR_SUCCESS)
		{
			return "";
		}

		for (DWORD i = 0; i < count; i++)
		{
			auto valCPU = bufferData[i].FmtValue.longValue;

#ifdef UNICODE
			QString convertedStr = QString::fromWCharArray(bufferData[i].szName);
#else
			QString convertedStr = QString::fromLocal8Bit(bufferData[i].szName);
#endif

			if (convertedStr != "_Total")
			{
				jCores.append(valCPU / 100.0f);
			}
			else
			{
				jResult["total"] = (double)valCPU;
			}
		}

		jResult["cores"] = jCores;
		return QJsonDocument(jResult).toJson(QJsonDocument::Compact);
	}
	catch (...)
	{

	}
	return "";
}

QString SystemPerformanceCounters::getRAM()
{
	try
	{
		QJsonObject jResult;

		if (!isInitialized)
		{
			init();
			return "";
		}

		MEMORYSTATUSEX memInfo{};
		memInfo.dwLength = sizeof(MEMORYSTATUSEX);
		if (GlobalMemoryStatusEx(&memInfo) == 0)
			return "";
		qint64 totalPhysMem = qint64(memInfo.ullTotalPhys) / (1024ll * 1024);
		qint64 physMemAv = qint64(memInfo.ullAvailPhys) / (1024ll * 1024);
		qint64 takenMem = totalPhysMem - physMemAv;
		qint64 aspect = (takenMem * 100) / totalPhysMem;

		jResult["totalPhysMem"] = (qint64) totalPhysMem;
		jResult["takenMem"] = (qint64)takenMem;
		jResult["aspect"] = aspect;

		return QJsonDocument(jResult).toJson(QJsonDocument::Compact);
	}
	catch (...)
	{

	}
	return "";
}
