/* Animation_Swirl.cpp
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

#include <effects/Animation_Swirl.h>

Animation_Swirl::Animation_Swirl(double rotationTime) :
	AnimationBase(),
	_rotationTime(rotationTime),
	_angle(0)
{
	SetSleepTime(100);
}

EffectDefinition Animation_Swirl::definition(const char* name, double rotationTime, int smoothingTime, int smoothingFrequency)
{
	EffectDefinition ed([rotationTime]() -> AnimationBase* { return new Animation_Swirl(rotationTime); }, smoothingTime, smoothingFrequency);
	ed.name = name;
	return ed;
}

bool Animation_Swirl::isRegistered =
	overglow::effects::REGISTER_EFFECT(Animation_Swirl::definition("Rainbow swirl", 20.0, 200, 25)) &&
	overglow::effects::REGISTER_EFFECT(Animation_Swirl::definition("Rainbow swirl fast", 7.0));

int Animation_Swirl::getSTime(int hyperLatchTime, int _rt, double steps)
{
	double rt = double(_rt);
	double sleepTime = std::max(0.1, rt) / steps;

	double minStepTime = double(std::max(hyperLatchTime, 15)) / 1000.0;

	if (minStepTime < 0.001)
		minStepTime = 0.001;

	if (minStepTime > sleepTime)
		sleepTime = minStepTime;

	return int(round(sleepTime * 1000.0));
}

void Animation_Swirl::Init(
	HyperImage& hyperImage,
	int hyperLatchTime
)
{
	hyperImage.resize(80, 45);

	SetSleepTime(getSTime(hyperLatchTime, static_cast<int>(_rotationTime)));

	_angle = 0;

	double x = 255.0 / 12;
	_gradient.push_back({ 0,   255, 0, 0, 255 });
	_gradient.push_back({ (uint8_t)(x*0.8),  255, 128, 0, 255 });
	_gradient.push_back({ (uint8_t)(x*2.2),  255, 255, 0, 255 });
	_gradient.push_back({ (uint8_t)(x*3.5),  128, 255, 0, 255 });
	_gradient.push_back({ (uint8_t)(x*4.3),  0,   255, 0, 255 });
	_gradient.push_back({ (uint8_t)(x*5.1), 0,   255, 128, 255 });
	_gradient.push_back({ (uint8_t)(x*6.0), 0,   255, 255, 255 });
	_gradient.push_back({ (uint8_t)(x*6.9), 0,   128, 255, 255 });
	_gradient.push_back({ (uint8_t)(x*7.9), 0,     0, 255, 255 });
	_gradient.push_back({ (uint8_t)(x*8.9), 128,   0, 255, 255 });
	_gradient.push_back({ (uint8_t)(x*10), 255,   0, 255, 255 });
	_gradient.push_back({ (uint8_t)(x*11), 255,   0, 128, 255 });
	_gradient.push_back({ 255, 255,   0,   0, 255 });
}

bool Animation_Swirl::Play(HyperImage& painter)
{
	_angle += 1;
	if (_angle > 360)
		_angle = 0;

	std::vector<uint8_t> arr;
	for (const Animation_Swirl::SwirlGradient& item : _gradient)
	{
		arr.push_back(item.items[0]);
		arr.push_back(item.items[1]);
		arr.push_back(item.items[2]);
		arr.push_back(item.items[3]);
		arr.push_back(item.items[4]);
	}
	painter.conicalFill(std::max(std::min(_angle, 360), 0), arr, true);

	return true;
}
