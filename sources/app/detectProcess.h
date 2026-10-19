#pragma once

#include <QString>
#include <QStringList>

// psapi.h requires windows.h to be included
#include <Windows.h>
#include <Psapi.h>
#include <tlhelp32.h>

QStringList getProcessIdsByProcessName(const char *processName)
{
	QStringList listOfPids;

	// https://docs.microsoft.com/en-us/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes
	/* Take a snapshot of all processes in the system */
	HANDLE hProcessSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
	if(hProcessSnap == INVALID_HANDLE_VALUE)
	{
		return {};
	}

	PROCESSENTRY32W pe32{};
	pe32.dwSize = sizeof(PROCESSENTRY32W);

	/* Retrieve information about the first process */
	if(!Process32FirstW(hProcessSnap, &pe32))
	{
		CloseHandle(hProcessSnap);
		return {};
	}

	/* Walk through the snapshot of processes */
	do
	{
		if (QString::compare( processName, QString::fromWCharArray(pe32.szExeFile), Qt::CaseInsensitive) == 0)
			listOfPids.append(QString::number(pe32.th32ProcessID));

	} while(Process32NextW(hProcessSnap, &pe32));

	CloseHandle(hProcessSnap);

	return listOfPids;
}
