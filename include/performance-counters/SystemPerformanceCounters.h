#pragma once

#ifndef PCH_ENABLED
	#include <QObject>
	#include <QList>
	#include <QDateTime>
	#include <QJsonObject>
	#include <QString>
	#include <QJsonArray>
	#include <QJsonDocument>

	#include <algorithm>
#endif

class SystemPerformanceCounters
{
	private:
		bool isInitialized = false;
		void init();		

	public:
		~SystemPerformanceCounters();

		QString getCPU();
		QString getRAM();
};
