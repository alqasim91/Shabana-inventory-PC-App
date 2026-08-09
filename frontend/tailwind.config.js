/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      fontFamily: {
        sans: ['"IBM Plex Sans Arabic"', 'Tahoma', 'Arial', 'sans-serif'],
      },
      colors: {
        sand: '#F7F3EC',
        ink: '#2B2621',
        muted: '#6B6258',
        faint: '#9A9284',
        border: '#E4DDD0',
        'border-soft': '#F3EFE6',
        'row-alt': '#FAF7F1',
        teal: {
          DEFAULT: '#0E6F63',
          hover: '#0B5449',
          dark: '#0E3B36',
          soft: '#E7F1F1',
          light: '#9FC4BC',
          faint: '#7FAFA5',
          muted: '#5E8F86',
        },
        amber: {
          DEFAULT: '#C8860D',
          text: '#8A5A06',
          soft: '#F3ECDF',
          'soft-text': '#7A5A1E',
        },
        success: {
          soft: '#E6F2EA',
          text: '#1F7A4D',
        },
      },
      borderRadius: {
        card: '14px',
        pill: '999px',
      },
    },
  },
  plugins: [],
};
