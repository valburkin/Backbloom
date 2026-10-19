#pragma once

#include <effects/AnimationBase.h>

// Fades in from colorStart to colorEnd and out again (repeat = 0: forever), preset: "Breath"
class Animation_Fade : public AnimationBase
{
public:
	struct Params
	{
		Point3d colorStart;
		Point3d colorEnd;
		double  fadeInTime;		// [ms]
		double  fadeOutTime;	// [ms]
		double  colorStartTime;	// time to hold colorStart [ms]
		double  colorEndTime;	// time to hold colorEnd [ms]
	};

	explicit Animation_Fade(const Params& params);

	void Init(
		HyperImage& hyperImage,
		int hyperLatchTime) override;

	bool Play(HyperImage& painter) override;
	bool hasLedData(QVector<ColorRgb>& buffer) override;

private:

	void SetPoint(Point3d point);
	int     currentStep;
	Point3d current;
	int     incrementIn;
	double  sleepTimeIn;
	int     incrementOut;
	double  sleepTimeOut;
	int     repeatCounter;
	double  steps;
	std::vector<Point3d> colors;

	static EffectDefinition definition(const char* name, const Params& params);
	static bool isRegistered;

	double  fadeInTime;
	double  fadeOutTime;
	Point3d colorStart;
	Point3d colorEnd;
	double  colorStartTime;
	double  colorEndTime;
	int     repeat;
	bool    maintainEndCol;
	double  minStepTime;

	int     indexer1;
	double  indexer2;
	int     indexer3;
	double  indexer4;
	int     indexer6;
};
