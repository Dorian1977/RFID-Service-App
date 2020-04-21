#pragma once
#include "BlowFish.h"
using namespace System;

namespace BlowFishClassLibrary {
	public ref class BlowFishClass
	{
		BlowFish* blowFish;
	public:
		// TODO: Add your methods for this class here.
		DWORD Encode(BYTE* key, int keybytes, BYTE* pInput, BYTE* pOutput, DWORD lSize);
		void Decode(BYTE* key, int keybytes, BYTE* pInput, BYTE* pOutput, DWORD lSize);
	};
}
